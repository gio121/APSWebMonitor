using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApsMonitor.Models;

// ─────────────────────────────────────────────────────────────────────────────
// Root entity stored in DB
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Entidad raíz persistida en la tabla TrdpConfigs.
/// Los objetos complejos se almacenan como columnas JSON.
/// </summary>
public class TrdpSessionConfig
{
    public int Id { get; set; }

    /// <summary>Nombre descriptivo de esta configuración (p.ej. "Ramal 1 - Producción")</summary>
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Descripción libre</summary>
    public string Descripcion { get; set; } = string.Empty;

    /// <summary>Fecha de última modificación</summary>
    public DateTime UltimaModificacion { get; set; } = DateTime.UtcNow;

    // ── Opciones globales ──────────────────────────────────────────────────
    public bool UseDynamicMapping { get; set; } = true;

    // ── Secciones (serializadas como JSON) ────────────────────────────────
    public string ControlFrameJson    { get; set; } = "{}";
    public string CommsDatasetsJson   { get; set; } = "{}";
    public string NetworksJson        { get; set; } = "[]";

    // ── Helpers de deserialización (no mapeados a columna) ───────────────
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public TrdpControlFrameConfig ControlFrame
    {
        get => TryDeserialize<TrdpControlFrameConfig>(ControlFrameJson) ?? new();
        set => ControlFrameJson = JsonSerializer.Serialize(value, TrdpJsonOptions.Default);
    }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public Dictionary<string, TrdpDataset> CommsDatasets
    {
        get => TryDeserialize<Dictionary<string, TrdpDataset>>(CommsDatasetsJson) ?? new();
        set => CommsDatasetsJson = JsonSerializer.Serialize(value, TrdpJsonOptions.Default);
    }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public List<TrdpNetwork> Networks
    {
        get => TryDeserialize<List<TrdpNetwork>>(NetworksJson) ?? new();
        set => NetworksJson = JsonSerializer.Serialize(value, TrdpJsonOptions.Default);
    }

    private static T? TryDeserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, TrdpJsonOptions.Default); }
        catch { return default; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Control Frame
// ─────────────────────────────────────────────────────────────────────────────

public class TrdpControlFrameConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public List<TrdpControlVariable> Variables { get; set; } = new();
}

public class TrdpControlVariable
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "uint16";

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Size { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// Comms Datasets
// ─────────────────────────────────────────────────────────────────────────────

public class TrdpDataset
{
    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("variables")]
    public List<TrdpDatasetVariable> Variables { get; set; } = new();

    [JsonPropertyName("variable_links")]
    public List<TrdpVariableLink> VariableLinks { get; set; } = new();

    [JsonPropertyName("control_commands")]
    public List<TrdpControlCommandRule> ControlCommands { get; set; } = new();
}

public class TrdpDatasetVariable
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "uint8";

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Size { get; set; }

    [JsonPropertyName("scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Scale { get; set; }

    [JsonPropertyName("offset_val")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OffsetVal { get; set; }

    [JsonPropertyName("endian")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Endian { get; set; }

    [JsonPropertyName("unit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Unit { get; set; }

    [JsonPropertyName("reverse_bits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReverseBits { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// Variable Links
// ─────────────────────────────────────────────────────────────────────────────

public class TrdpVariableLink
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "analog"; // analog | bit_to_bit | condition_to_bit | constant | direct_copy | sequence | logic_to_bit

    // Source
    [JsonPropertyName("source_control_var")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceControlVar { get; set; }

    [JsonPropertyName("source_bit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceBit { get; set; }

    // Target
    [JsonPropertyName("target_comms_var")]
    public string TargetCommsVar { get; set; } = string.Empty;

    [JsonPropertyName("target_bit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TargetBit { get; set; }

    // Analog
    [JsonPropertyName("scale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Scale { get; set; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Offset { get; set; }

    // Constant / direct_copy
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Value { get; set; }

    [JsonPropertyName("length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Length { get; set; }

    // bit_to_bit / condition_to_bit flags
    [JsonPropertyName("set_only")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SetOnly { get; set; }

    // condition_to_bit
    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; } // equals | in_list

    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<double>? Values { get; set; }

    [JsonPropertyName("comment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; set; }

    // logic_to_bit
    [JsonPropertyName("operator")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operator { get; set; } // OR | AND

    [JsonPropertyName("inputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TrdpLogicInput>? Inputs { get; set; }
}

/// <summary>
/// Input for a logic_to_bit link: can be a bit reference or a condition on a control variable.
/// </summary>
public class TrdpLogicInput
{
    [JsonPropertyName("source_control_var")]
    public string SourceControlVar { get; set; } = string.Empty;

    [JsonPropertyName("source_bit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceBit { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; } // equals | in_list

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Value { get; set; }

    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<double>? Values { get; set; }

    /// <summary>Derived input mode for UI: "bit" or "condition"</summary>
    [JsonIgnore]
    public string Mode => SourceBit.HasValue ? "bit" : "condition";
}

/// <summary>
/// Result returned by TrdpDatasetVarDialog when the user saves (variable + updated links for it).
/// </summary>
public class TrdpVarEditResult
{
    public TrdpDatasetVariable Variable { get; set; } = new();
    public List<TrdpVariableLink> Links { get; set; } = new();
}

// ─────────────────────────────────────────────────────────────────────────────
// Networks
// ─────────────────────────────────────────────────────────────────────────────

public class TrdpNetwork
{
    [JsonPropertyName("received_ip")]
    public string ReceivedIp { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("publishers")]
    public List<TrdpPublisher> Publishers { get; set; } = new();

    [JsonPropertyName("subscribers")]
    public List<TrdpSubscriber> Subscribers { get; set; } = new();
}

public class TrdpPublisher
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("com_id")]
    public long ComId { get; set; }

    [JsonPropertyName("dataset")]
    public string Dataset { get; set; } = string.Empty;

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

public class TrdpSubscriber
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("com_id")]
    public long ComId { get; set; }

    [JsonPropertyName("dataset")]
    public string Dataset { get; set; } = string.Empty;
}

// ─────────────────────────────────────────────────────────────────────────────
// Control Commands (Reglas a Control)
// ─────────────────────────────────────────────────────────────────────────────

public class TrdpControlCommandRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = "rising_edge";

    [JsonPropertyName("src_var")]
    public string SrcVar { get; set; } = string.Empty;

    [JsonPropertyName("bit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Bit { get; set; }

    [JsonPropertyName("invert_bit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InvertBit { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Value { get; set; }

    [JsonPropertyName("value_list")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<double>? ValueList { get; set; }

    [JsonPropertyName("control_conditions")]
    public List<TrdpControlCondition> ControlConditions { get; set; } = new();

    [JsonPropertyName("logic_op")]
    public string LogicOp { get; set; } = "AND";

    [JsonPropertyName("conditions")]
    public List<TrdpControlCondition> Conditions { get; set; } = new();

    [JsonPropertyName("min_interval_ms")]
    public int MinIntervalMs { get; set; } = 1000;

    [JsonPropertyName("action_type")]
    public string ActionType { get; set; } = "send_scmd";

    [JsonPropertyName("command")]
    public TrdpScmdAction Command { get; set; } = new();
}

public class TrdpControlCondition
{
    [JsonPropertyName("control_var")]
    public string ControlVar { get; set; } = string.Empty;

    [JsonPropertyName("condition")]
    public string Condition { get; set; } = "equals";

    [JsonPropertyName("value")]
    public double Value { get; set; }
}

public class TrdpScmdAction
{
    [JsonPropertyName("frame_type")]
    public string FrameType { get; set; } = "0x7A";

    [JsonPropertyName("sub_cmd")]
    public string SubCmd { get; set; } = "0x0005";

    [JsonPropertyName("data")]
    public double Data { get; set; }

    [JsonPropertyName("data_from_var")]
    public string DataFromVar { get; set; } = string.Empty;

    [JsonPropertyName("use_16bit_data")]
    public bool Use16BitData { get; set; }

    [JsonPropertyName("time_var")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeVar { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Real-time TRDP Frame Message (WebSocket / REST RX)
// ─────────────────────────────────────────────────────────────────────────────

public class TrdpFrameMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "trdp_frame";

    [JsonPropertyName("dataset")]
    public string Dataset { get; set; } = string.Empty;

    [JsonPropertyName("com_id")]
    public long ComId { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("raw_hex")]
    public string RawHex { get; set; } = string.Empty;

    [JsonPropertyName("raw_bytes")]
    public List<byte> RawBytes { get; set; } = new();

    [JsonPropertyName("parsed")]
    public Dictionary<string, JsonElement>? Parsed { get; set; }

    [JsonPropertyName("snapshot")]
    public bool Snapshot { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared JSON options
// ─────────────────────────────────────────────────────────────────────────────

public static class TrdpJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy       = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented              = false,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        Encoder                    = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters                 = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    /// <summary>Human-readable options para export/import del JSON de configuración.</summary>
    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy       = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        Encoder                    = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters                 = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}
