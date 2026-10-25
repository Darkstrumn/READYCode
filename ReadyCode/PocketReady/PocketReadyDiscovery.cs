// Licensed under the MIT License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReadyCode.PocketReady;

/// <summary>
/// Discovery contract for Pocket READY. console protocol 1. Unknown additive JSON
/// properties/minor versions are allowed; incompatible identity/major versions fail.
/// This describes capabilities, not a connection lease or completed execution.
/// </summary>
public sealed record PocketReadyDiscovery
{
    public required ProtocolInfo Protocol { get; init; }
    public required string InstanceId { get; init; }
    public required ProductInfo Product { get; init; }
    public required LanguageInfo Language { get; init; }
    public required DeviceInfo Device { get; init; }
    public required SessionInfo Session { get; init; }
    public required CapabilityInfo Capabilities { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };

    /// <summary>Parses a bounded UTF-8 response after transport validation.</summary>
    public static PocketReadyDiscovery Parse(string json)
    {
        PocketReadyDiscovery? discovery;
        try
        {
            discovery = JsonSerializer.Deserialize<PocketReadyDiscovery>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new PocketReadyProtocolException("Pocket READY. discovery is malformed or incomplete.", exception);
        }

        if (discovery is null)
        {
            throw new PocketReadyProtocolException("Pocket READY. discovery is empty.");
        }

        discovery.Validate();
        return discovery;
    }

    private void Validate()
    {
        if (Protocol is null || Protocol.Name != "pocket-ready-console" || Protocol.Major != 1 || Protocol.Minor < 0)
        {
            throw new PocketReadyProtocolException("The device does not support Pocket READY. console protocol 1.");
        }

        if (InstanceId is null || !Guid.TryParseExact(InstanceId, "D", out _) ||
            InstanceId[14] != '4' || !"89abAB".Contains(InstanceId[19]))
        {
            throw new PocketReadyProtocolException("Discovery has no valid session instance identity.");
        }

        if (Product is null || Product.Name != "Pocket READY." || Product.Version is null ||
            !Regex.IsMatch(Product.Version, @"^[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant))
        {
            throw new PocketReadyProtocolException("Discovery does not identify a supported Pocket READY. product.");
        }

        if (Language is null || Language.Id != "pocket-ready-basic" || Language.SourceEncoding != "UTF-8" ||
            Language.SourceCapacityBytes <= 0 || Language.LineLabels != "optional")
        {
            throw new PocketReadyProtocolException("Discovery has an unsupported BASIC source format.");
        }

        if (Device is null || Device.Abi < 1 || Device.SpriteSlots < 0 || string.IsNullOrWhiteSpace(Device.Audio))
        {
            throw new PocketReadyProtocolException("Discovery has invalid device capabilities.");
        }

        if (Session is null || Session.Generation < 0 || Session.Generation > 9007199254740991L ||
            Session.Phase is not ("idle" or "running"))
        {
            throw new PocketReadyProtocolException("Discovery has invalid execution state.");
        }

        if (Capabilities is null)
        {
            throw new PocketReadyProtocolException("Discovery has no capability declaration.");
        }

        ValidateNames(Device.GraphicsPlanes);
        ValidateNames(Capabilities.Transports);
        ValidateNames(Capabilities.ControlOperations);
        ValidateNames(Capabilities.SourceFormats);
        if (!Capabilities.SourceFormats.Contains("basic-utf8", StringComparer.Ordinal))
        {
            throw new PocketReadyProtocolException("The device does not accept Pocket READY. UTF-8 source.");
        }
    }

    private static void ValidateNames(string[]? values)
    {
        if (values is null || values.Length > 64 ||
            values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 80) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new PocketReadyProtocolException("Discovery contains an invalid capability list.");
        }
    }
}

public sealed record ProtocolInfo
{
    public required string Name { get; init; }
    public required int Major { get; init; }
    public required int Minor { get; init; }
}

public sealed record ProductInfo
{
    public required string Name { get; init; }
    public required string Version { get; init; }
}

public sealed record LanguageInfo
{
    public required string Id { get; init; }
    public required string SourceEncoding { get; init; }
    public required int SourceCapacityBytes { get; init; }
    public required string LineLabels { get; init; }
}

public sealed record DeviceInfo
{
    public required int Abi { get; init; }
    public required string[] GraphicsPlanes { get; init; }
    public required int SpriteSlots { get; init; }
    public required string Audio { get; init; }
}

public sealed record SessionInfo
{
    public required long Generation { get; init; }
    public required string Phase { get; init; }
    public required bool Pending { get; init; }
}

public sealed record CapabilityInfo
{
    public required string[] Transports { get; init; }
    public required string[] ControlOperations { get; init; }
    public required string[] SourceFormats { get; init; }
    public required bool Output { get; init; }
    public required bool LocatedDiagnostics { get; init; }
    public required bool Filesystem { get; init; }
    public required bool VirtualMemory { get; init; }
    public required bool CpuDebug { get; init; }
    public required bool Assembly { get; init; }
}

/// <summary>A bounded discovery response could not be interpreted safely.</summary>
public sealed class PocketReadyProtocolException : Exception
{
    public PocketReadyProtocolException(string message) : base(message) { }

    public PocketReadyProtocolException(string message, Exception innerException)
        : base(message, innerException) { }
}
