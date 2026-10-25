// Licensed under the MIT License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReadyCode.PocketReady;

/// <summary>Version-one wire identities, bounds and exact-source helpers; no C64 tokenization.</summary>
public static class PocketReadyContract
{
    public const int SourceCapacityBytes = 655360;
    public const long MaxInteger = 9007199254740991L;
    public static IReadOnlyList<string> Operations { get; } = Array.AsReadOnly(new[] { "load", "loadRun", "run", "stop", "command", "input" });
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16 };

    internal static T Parse<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json) ?? throw new JsonException("Empty response");
        }
        catch (JsonException error)
        {
            throw new PocketReadyProtocolException("Malformed or incomplete Pocket READY. response.", error);
        }
    }

    internal static bool IsInstance(string? value) => value is not null &&
        Regex.IsMatch(value, @"\A[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z");
    internal static bool IsId(string? value) => value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9_.:-]{1,80}\z");
    internal static bool IsInteger(long value) => value >= 0 && value <= MaxInteger;

    /// <summary>Matches the interpreter's UTF-16 FNV diagnostic identity, not a security hash.</summary>
    public static string SourceId(string text)
    {
        uint hash = 2166136261;
        foreach (char value in text) hash = unchecked((hash ^ value) * 16777619);
        return text.Length.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToString("x", CultureInfo.InvariantCulture);
    }
}

public sealed record PocketReadyControl
{
    public required string InstanceId { get; init; }
    public required string Controller { get; init; }
    public required long Lease { get; init; }
    public required long Generation { get; init; }
    public required string Phase { get; init; }
    public required bool Pending { get; init; }

    public void Validate()
    {
        if (!PocketReadyContract.IsInstance(InstanceId) || Controller != "lan-console" ||
            !PocketReadyContract.IsInteger(Lease) || Lease == 0 || !PocketReadyContract.IsInteger(Generation) ||
            Phase is not ("idle" or "running"))
            throw new PocketReadyProtocolException("Invalid controlling session identity or state.");
    }
}

/// <summary>Keep this exact immutable envelope for a deliberate retry after an uncertain outcome.</summary>
public sealed record PocketReadyOperation
{
    public string Kind => "operation";
    public required string InstanceId { get; init; }
    public required string Controller { get; init; }
    public required long Lease { get; init; }
    public required long Generation { get; init; }
    public required string Id { get; init; }
    public required string Operation { get; init; }
    public required string Text { get; init; }

    public void Validate()
    {
        if (!PocketReadyContract.IsInstance(InstanceId) || Controller != "lan-console" || Lease <= 0 ||
            !PocketReadyContract.IsInteger(Lease) || !PocketReadyContract.IsInteger(Generation) ||
            !PocketReadyContract.IsId(Id) || !PocketReadyContract.Operations.Contains(Operation) || Text is null)
            throw new ArgumentException("Invalid operation envelope.");
        if (Operation is "run" or "stop" && Text.Length != 0)
            throw new ArgumentException("RUN and STOP do not accept text.");
        if (Operation == "input" && (Text.Length == 0 || Text.Length > 16))
            throw new ArgumentException("Input requires 1–16 UTF-16 code units.");
        if (PocketReadyContract.Utf8.GetByteCount(Text) > PocketReadyContract.SourceCapacityBytes)
            throw new ArgumentException("BASIC source exceeds 640 KiB UTF-8.");
    }

    public static PocketReadyOperation Create(PocketReadyControl state, string operation, string text = "")
    {
        state.Validate();
        var request = new PocketReadyOperation { InstanceId = state.InstanceId, Controller = state.Controller,
            Lease = state.Lease, Generation = state.Generation, Id = "desktop:" + Guid.NewGuid().ToString("D"), Operation = operation, Text = text };
        request.Validate();
        return request;
    }
}

public sealed record PocketReadyReceipt
{
    public required string Id { get; init; }
    public required long Token { get; init; }
    public required string Status { get; init; }
    public required string Operation { get; init; }
    public string? Error { get; init; }

    internal void Validate()
    {
        if (!PocketReadyContract.IsId(Id) || Token <= 0 || !PocketReadyContract.IsInteger(Token) ||
            Status is not ("accepted" or "applied" or "rejected") ||
            !PocketReadyContract.Operations.Contains(Operation) || Error?.Length > 4096)
            throw new PocketReadyProtocolException("Invalid application receipt.");
    }
}

public sealed record PocketReadyOutput
{
    public required bool Gap { get; init; }
    public required long Next { get; init; }
    public required long Latest { get; init; }
    public required PocketReadyEvent[] Events { get; init; }

    internal void Validate(long after)
    {
        if (!PocketReadyContract.IsInteger(Next) || !PocketReadyContract.IsInteger(Latest) ||
            Next < after || Latest < Next || Events is null || Events.Length > 2)
            throw new PocketReadyProtocolException("Invalid output cursor/page.");
        long previous = after;
        foreach (var item in Events)
        {
            if (item is null || item.Sequence <= previous || item.Sequence > Next ||
                item.RunToken <= 0 || !PocketReadyContract.IsInteger(item.RunToken) ||
                item.Type is not ("output" or "error") || item.Text is null ||
                PocketReadyContract.Utf8.GetByteCount(item.Text) > 16384)
                throw new PocketReadyProtocolException("Invalid output event.");
            item.Diagnostic?.Validate();
            previous = item.Sequence;
        }
        if (Next != previous) throw new PocketReadyProtocolException("Output next cursor does not match the page.");
    }
}

public sealed record PocketReadyEvent
{
    public required long Sequence { get; init; }
    public required long RunToken { get; init; }
    public required string Type { get; init; }
    public required string Text { get; init; }
    public PocketReadyDiagnostic? Diagnostic { get; init; }
}

public sealed record PocketReadyDiagnostic
{
    public PocketReadyLocation? Location { get; init; }
    public required PocketReadyLocation?[] Frames { get; init; }
    internal void Validate()
    {
        if (Frames is null || Frames.Length > 8) throw new PocketReadyProtocolException("Too many diagnostic frames.");
        Location?.Validate();
        foreach (var frame in Frames) frame?.Validate();
    }
}

public sealed record PocketReadyLocation
{
    public string? Kind { get; init; }
    public string? SourceName { get; init; }
    public string? SourceId { get; init; }
    public string? Label { get; init; }
    public string? Statement { get; init; }
    public string? Routine { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    internal void Validate()
    {
        if (new[] { Kind, SourceName, SourceId, Label, Routine }.Any(value => value?.Length > 128) ||
            Statement?.Length > 512 || Line is <= 0 || Column is <= 0)
            throw new PocketReadyProtocolException("Invalid diagnostic location.");
    }
}

/// <summary>A broker rejection is distinct from HTTP failure or an uncertain delivery timeout.</summary>
public sealed class PocketReadyRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class PocketReadyOutcomeUnknownException(string message) : Exception(message) { }
