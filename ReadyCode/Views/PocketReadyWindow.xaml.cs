// Licensed under the MIT License. See LICENSE in the project root.

using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ReadyCode.Editor;
using ReadyCode.Models;
using ReadyCode.PocketReady;

namespace ReadyCode.Views;

/// <summary>
/// Window/dispatcher-owned connection controller. Network work lives in the client;
/// the gate serializes observation and actions against the phone's one-delivery mailbox.
/// No token is persisted. Closing cancels and drains owned work before disposing resources.
/// </summary>
public partial class PocketReadyWindow : Window
{
    private readonly EditorTab _tab;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly PocketReadyClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ObservableCollection<PocketReadyEvent> _diagnostics = [];
    private CancellationTokenSource? _action;
    private CancellationTokenSource? _observation;
    private Task _actionTask = Task.CompletedTask;
    private Task _observationTask = Task.CompletedTask;
    private PocketReadyControl? _state;
    private PocketReadyDiscovery? _discovery;
    private PocketReadyOperation? _lastOperation;
    private Uri? _baseUri;
    private string _token = string.Empty;
    private string? _deployedSource;
    private long _after;
    private bool _busy;
    private bool _uncertain;
    private bool _closed;
    private bool _initializing = true;

    public PocketReadyWindow(EditorTab tab)
    {
        _tab = tab;
        _client = new PocketReadyClient(_http);
        InitializeComponent();
        SourceEditor.Document = tab.Document;
        SourceEditor.TextArea.TextView.LineTransformers.Add(new PocketReadyKeywordColorizer());
        DiagnosticsList.ItemsSource = _diagnostics;
        Title = "Pocket READY. — " + tab.FileName;
        _initializing = false;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save, (_, _) => Save_Click(this, new RoutedEventArgs())));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Save, new KeyGesture(Key.S, ModifierKeys.Control)));
        UpdateButtons();
    }

    private void SourceEditor_TextChanged(object? sender, EventArgs e)
    {
        if (!_initializing) _tab.IsModified = true;
    }

    private void UpdateButtons()
    {
        if (_closed) return;
        bool connected = _state is not null;
        ConnectionFields.IsEnabled = !_busy && !connected;
        ConnectButton.IsEnabled = !_busy && !connected;
        CancelButton.IsEnabled = _busy;
        ActionPanel.IsEnabled = connected && !_busy;
        CommandButton.IsEnabled = connected && !_busy && !_uncertain;
        InputButton.IsEnabled = connected && !_busy && !_uncertain;
        LoadButton.IsEnabled = connected && !_uncertain;
        LoadRunButton.IsEnabled = connected && !_uncertain;
        RunButton.IsEnabled = connected && !_uncertain;
        RetryButton.IsEnabled = _lastOperation is not null;
        QueryButton.IsEnabled = _lastOperation is not null;
        ObserveButton.IsEnabled = connected && _discovery?.Capabilities.Output == true;
        ObserveButton.Content = _observation is null ? "Observe output" : "Stop observation";
    }

    private void Append(string text)
    {
        if (_closed) return;
        LogOutput.AppendText(text);
        if (LogOutput.Text.Length > 65536)
            LogOutput.Text = "[older desktop output trimmed]\n" + LogOutput.Text[^60000..];
        LogOutput.ScrollToEnd();
    }

    private async Task RunActionAsync(Func<CancellationToken, Task> action)
    {
        if (_busy || _closed) return;
        _busy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _action = cancellation;
        UpdateButtons();
        bool entered = false;
        try
        {
            await _gate.WaitAsync(cancellation.Token);
            entered = true;
            await action(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) StatusText.Text = _uncertain
                ? "Request cancelled. An action may already have applied; query/retry its original ID."
                : "Request cancelled.";
        }
        catch (Exception error)
        {
            if (!_closed) StatusText.Text = error.Message;
        }
        finally
        {
            if (entered) _gate.Release();
            _action = null;
            _busy = false;
            UpdateButtons();
        }
    }

    private void StartAction(Func<CancellationToken, Task> action) => _actionTask = RunActionAsync(action);

    private void Connect_Click(object sender, RoutedEventArgs e) => StartAction(async cancellation =>
    {
        var address = new Uri(BaseUrl.Text.Trim(), UriKind.Absolute);
        var discovery = await _client.GetInfoAsync(address, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (_closed) return;
        _discovery = discovery;
        Append($"{discovery.Product.Name} {discovery.Product.Version} — {discovery.InstanceId}\n");
        if (string.IsNullOrWhiteSpace(AccessToken.Password) || discovery.Capabilities.ControlOperations.Length == 0)
        {
            StatusText.Text = "Discovery succeeded. Allow control on the phone and enter its temporary token, then connect again.";
            return;
        }
        string token = AccessToken.Password.Trim();
        var state = await _client.GetControlAsync(address, token, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (state.InstanceId != discovery.InstanceId)
            throw new PocketReadyProtocolException("The phone restarted during discovery; rediscover before connecting.");
        if (_closed) return;
        _baseUri = address;
        _token = token;
        _state = state;
        _after = 0;
        _lastOperation = null;
        _uncertain = false;
        _deployedSource = null;
        StatusText.Text = "Connected to the phone's granted BASIC session. Applied receipts are not program completion.";
    });

    private async Task RefreshStateAsync(CancellationToken cancellation)
    {
        var previous = _state ?? throw new InvalidOperationException("Connect to a granted session first.");
        var current = await _client.GetControlAsync(_baseUri!, _token, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (current.InstanceId != previous.InstanceId || current.Lease != previous.Lease)
            throw new PocketReadyProtocolException("Control identity changed. Disconnect and explicitly reconnect; old actions will not be transferred.");
        _state = current;
    }

    private async Task ExecuteAsync(PocketReadyOperation operation, CancellationToken cancellation)
    {
        PocketReadyReceipt receipt;
        Append($"[request {operation.Operation} {operation.Id}, instance {operation.InstanceId}, generation {operation.Generation}]\n");
        try
        {
            receipt = await _client.ExecuteAsync(_baseUri!, _token, operation, cancellation);
            cancellation.ThrowIfCancellationRequested();
        }
        catch (PocketReadyRequestException)
        {
            _uncertain = false;
            throw;
        }
        catch
        {
            _uncertain = true;
            throw;
        }
        _uncertain = receipt.Status == "accepted";
        if (receipt.Status == "applied" && operation.Operation is "load" or "loadRun")
            _deployedSource = operation.Text;
        Append($"[{receipt.Operation} {receipt.Id}: {receipt.Status}{(receipt.Error is null ? "" : " — " + receipt.Error)}]\n");
        await RefreshStateAsync(cancellation);
        if (!_closed) StatusText.Text = $"{receipt.Status}; phone is {_state!.Phase}. Output is available through Observe output.";
    }

    private void NewOperation(string operation, string text = "") => StartAction(async cancellation =>
    {
        if (_uncertain && operation != "stop") throw new InvalidOperationException("Resolve the uncertain action with Query receipt or Retry exact request first.");
        await RefreshStateAsync(cancellation);
        if (_discovery?.Capabilities.ControlOperations.Contains(operation) != true)
            throw new InvalidOperationException("The device did not advertise this operation.");
        var request = PocketReadyOperation.Create(_state!, operation, text);
        var unresolved = _uncertain ? _lastOperation : null;
        if (unresolved is null) _lastOperation = request;
        try
        {
            await ExecuteAsync(request, cancellation);
        }
        finally
        {
            // STOP may be needed urgently, but must not erase the uncertain action's
            // original identity. Query/retry still refers to that action afterward.
            if (unresolved is not null)
            {
                _lastOperation = unresolved;
                _uncertain = true;
                if (!_closed) StatusText.Text += " Resolve the original uncertain request before new work.";
            }
        }
    });

    private void Load_Click(object sender, RoutedEventArgs e) => NewOperation("load", SourceEditor.Text);
    private void LoadRun_Click(object sender, RoutedEventArgs e) => NewOperation("loadRun", SourceEditor.Text);
    private void Run_Click(object sender, RoutedEventArgs e) => NewOperation("run");
    private void Stop_Click(object sender, RoutedEventArgs e) => NewOperation("stop");
    private void Command_Click(object sender, RoutedEventArgs e) => NewOperation("command", CommandText.Text);
    private void Input_Click(object sender, RoutedEventArgs e) => NewOperation("input", ProgramInput.Text);
    private void Retry_Click(object sender, RoutedEventArgs e) => StartAction(async cancellation =>
    {
        var request = _lastOperation ?? throw new InvalidOperationException("No original request is retained.");
        await RefreshStateAsync(cancellation);
        await ExecuteAsync(request, cancellation);
    });

    private void Query_Click(object sender, RoutedEventArgs e) => StartAction(async cancellation =>
    {
        await RefreshStateAsync(cancellation);
        var operation = _lastOperation ?? throw new InvalidOperationException("No original request is retained.");
        var receipt = await _client.GetReceiptAsync(_baseUri!, _token, _state!, operation.Id, cancellation);
        _uncertain = receipt is null || receipt.Status == "accepted";
        if (receipt?.Status == "applied" && operation.Operation is "load" or "loadRun") _deployedSource = operation.Text;
        if (!_closed) StatusText.Text = receipt is null
            ? "No retained receipt. This does not prove the action never ran; do not blindly repeat it under a new ID."
            : $"Original operation is {receipt.Status}: {receipt.Error}";
    });

    private void Observe_Click(object sender, RoutedEventArgs e)
    {
        if (_observation is not null) { _observation.Cancel(); return; }
        _observation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _observationTask = ObserveAsync(_observation);
        UpdateButtons();
    }

    private async Task ObserveAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await _gate.WaitAsync(cancellation.Token);
                try
                {
                    var state = _state ?? throw new InvalidOperationException("The connection was closed.");
                    var page = await _client.ReadOutputAsync(_baseUri!, _token, state, _after, cancellation.Token);
                    if (page.Gap) Append("[phone output history gap — some events were missed]\n");
                    foreach (var item in page.Events)
                    {
                        Append(item.Text);
                        if (item.Diagnostic is not null && !_closed)
                        {
                            _diagnostics.Add(item);
                            if (_diagnostics.Count > 100) _diagnostics.RemoveAt(0);
                        }
                    }
                    _after = page.Next;
                }
                finally { _gate.Release(); }
                await Task.Delay(250, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) StatusText.Text = "Observation stopped: " + error.Message; }
        finally
        {
            if (ReferenceEquals(_observation, cancellation)) _observation = null;
            cancellation.Dispose();
            UpdateButtons();
        }
    }

    private void Diagnostic_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var location = (DiagnosticsList.SelectedItem as PocketReadyEvent)?.Diagnostic?.Location;
        string current = SourceEditor.Text;
        if (location?.Line is not int line || line > SourceEditor.Document.LineCount ||
            location.Kind != "program" || location.SourceId != PocketReadyContract.SourceId(current) || current != _deployedSource)
        {
            StatusText.Text = "The error refers to another source revision/module; no cursor jump was made.";
            return;
        }
        var target = SourceEditor.Document.GetLineByNumber(line);
        int column = Math.Clamp(location.Column ?? 1, 1, target.Length + 1);
        SourceEditor.CaretOffset = target.Offset + column - 1;
        SourceEditor.ScrollTo(line, column);
        SourceEditor.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => StartAction(async cancellation =>
    {
        var dialog = new SaveFileDialog { Filter = "Pocket READY. BASIC (*.pbas)|*.pbas|BASIC source (*.bas)|*.bas", DefaultExt = ".pbas", FileName = _tab.FileName };
        if (dialog.ShowDialog(this) != true) return;
        string text = SourceEditor.Text;
        await PocketReadySourceFile.SaveAsync(dialog.FileName, text, cancellation);
        _tab.FilePath = dialog.FileName;
        if (_tab.Document.Text == text) _tab.IsModified = false;
        if (!_closed)
        {
            Title = "Pocket READY. — " + _tab.FileName;
            StatusText.Text = "UTF-8 source saved. The phone's in-memory program is independent.";
        }
    });

    private void Cancel_Click(object sender, RoutedEventArgs e) => _action?.Cancel();
    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        _action?.Cancel();
        _observation?.Cancel();
        _state = null;
        _token = string.Empty;
        AccessToken.Password = string.Empty;
        StatusText.Text = "Disconnected locally. The phone may still be running; use RUN STOP there to reclaim control.";
        UpdateButtons();
    }

    protected override async void OnClosed(EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _token = string.Empty;
        AccessToken.Password = string.Empty;
        base.OnClosed(e);
        try { await Task.WhenAll(_actionTask, _observationTask); }
        finally
        {
            _http.Dispose();
            _gate.Dispose();
            _lifetime.Dispose();
        }
    }
}
