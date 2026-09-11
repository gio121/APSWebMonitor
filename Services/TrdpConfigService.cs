using ApsMonitor.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ApsMonitor.Services;

/// <summary>
/// Servicio para gestionar configuraciones de sesión TRDP.
/// Proporciona CRUD sobre la tabla TrdpConfigs e import/export desde JSON.
/// </summary>
public class TrdpConfigService
{
    private readonly IDbContextFactory<Data.ApsDbContext> _dbContextFactory;

    public TrdpConfigService(IDbContextFactory<Data.ApsDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    // ── CRUD básico ──────────────────────────────────────────────────────────

    public async Task<List<TrdpSessionConfig>> GetAllAsync()
    {
        using var ctx = await _dbContextFactory.CreateDbContextAsync();
        return await ctx.TrdpConfigs.OrderByDescending(c => c.UltimaModificacion).ToListAsync();
    }

    public async Task<TrdpSessionConfig?> GetByIdAsync(int id)
    {
        using var ctx = await _dbContextFactory.CreateDbContextAsync();
        return await ctx.TrdpConfigs.FindAsync(id);
    }

    public async Task<TrdpSessionConfig> SaveAsync(TrdpSessionConfig config)
    {
        config.UltimaModificacion = DateTime.UtcNow;
        using var ctx = await _dbContextFactory.CreateDbContextAsync();

        if (config.Id == 0)
            ctx.TrdpConfigs.Add(config);
        else
            ctx.TrdpConfigs.Update(config);

        await ctx.SaveChangesAsync();
        return config;
    }

    public async Task DeleteAsync(int id)
    {
        using var ctx = await _dbContextFactory.CreateDbContextAsync();
        var entity = await ctx.TrdpConfigs.FindAsync(id);
        if (entity is not null)
        {
            ctx.TrdpConfigs.Remove(entity);
            await ctx.SaveChangesAsync();
        }
    }

    // ── Import desde JSON de configuración ──────────────────────────────────

    /// <summary>
    /// Parsea el JSON de configuración TRDP (estructura del archivo de config proporcionado)
    /// y crea una nueva entidad <see cref="TrdpSessionConfig"/> lista para guardar.
    /// </summary>
    public TrdpSessionConfig ImportFromJson(string json, string nombre = "Config importada")
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var config = new TrdpSessionConfig
        {
            Nombre      = nombre,
            Descripcion = "Importada desde JSON",
            UltimaModificacion = DateTime.UtcNow
        };

        // use_dynamic_mapping
        if (root.TryGetProperty("use_dynamic_mapping", out var dynMap))
            config.UseDynamicMapping = dynMap.GetBoolean();

        // control_frame
        if (root.TryGetProperty("control_frame", out var cfEl))
        {
            var cf = ParseControlFrame(cfEl);
            config.ControlFrame = cf;
        }

        // comms_datasets
        if (root.TryGetProperty("comms_datasets", out var dsEl))
        {
            var datasets = new Dictionary<string, TrdpDataset>();
            foreach (var prop in dsEl.EnumerateObject())
                datasets[prop.Name] = ParseDataset(prop.Value);
            config.CommsDatasets = datasets;
        }

        // networks
        if (root.TryGetProperty("networks", out var netsEl))
        {
            var networks = new List<TrdpNetwork>();
            foreach (var net in netsEl.EnumerateArray())
                networks.Add(ParseNetwork(net));
            config.Networks = networks;
        }

        return config;
    }

    // ── Export a JSON ────────────────────────────────────────────────────────

    /// <summary>
    /// Serializa la configuración al formato JSON canónico del archivo de config.
    /// </summary>
    public string ExportToJson(TrdpSessionConfig config)
    {
        var doc = new
        {
            use_dynamic_mapping = config.UseDynamicMapping,
            control_frame       = config.ControlFrame,
            comms_datasets      = config.CommsDatasets,
            networks            = config.Networks
        };
        return JsonSerializer.Serialize(doc, TrdpJsonOptions.Pretty);
    }

    // ── Duplicar ─────────────────────────────────────────────────────────────

    public async Task<TrdpSessionConfig> DuplicateAsync(int id)
    {
        var original = await GetByIdAsync(id) ?? throw new InvalidOperationException("Config no encontrada");
        var copy = new TrdpSessionConfig
        {
            Nombre             = original.Nombre + " (copia)",
            Descripcion        = original.Descripcion,
            UseDynamicMapping  = original.UseDynamicMapping,
            ControlFrameJson   = original.ControlFrameJson,
            CommsDatasetsJson  = original.CommsDatasetsJson,
            NetworksJson       = original.NetworksJson,
            UltimaModificacion = DateTime.UtcNow
        };
        return await SaveAsync(copy);
    }

    // ── Parsers internos ─────────────────────────────────────────────────────

    private static TrdpControlFrameConfig ParseControlFrame(JsonElement el)
    {
        var cf = new TrdpControlFrameConfig
        {
            Name        = el.TryGetProperty("name",        out var n)   ? n.GetString() ?? "" : "",
            Size        = el.TryGetProperty("size",        out var sz)  ? sz.GetInt32()       : 0,
            Description = el.TryGetProperty("description", out var d)   ? d.GetString() ?? "" : "",
        };

        if (el.TryGetProperty("variables", out var vars))
        {
            foreach (var v in vars.EnumerateArray())
            {
                cf.Variables.Add(new TrdpControlVariable
                {
                    Id          = v.TryGetProperty("id",          out var id)   ? id.GetString() ?? ""  : "",
                    Offset      = v.TryGetProperty("offset",      out var off)  ? off.GetInt32()         : 0,
                    Type        = v.TryGetProperty("type",        out var t)    ? t.GetString() ?? "uint16" : "uint16",
                    Size        = v.TryGetProperty("size",        out var s)    ? s.GetInt32()           : null,
                    Description = v.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
                });
            }
        }
        return cf;
    }

    private static TrdpDataset ParseDataset(JsonElement el)
    {
        var ds = new TrdpDataset
        {
            Size        = el.TryGetProperty("size",        out var sz) ? sz.GetInt32()       : 0,
            Description = el.TryGetProperty("description", out var d)  ? d.GetString() ?? "" : ""
        };

        if (el.TryGetProperty("variables", out var vars))
        {
            foreach (var v in vars.EnumerateArray())
                ds.Variables.Add(ParseDatasetVariable(v));
        }

        if (el.TryGetProperty("variable_links", out var links))
        {
            foreach (var lnk in links.EnumerateArray())
                ds.VariableLinks.Add(ParseVariableLink(lnk));
        }

        if (el.TryGetProperty("control_commands", out var cmds))
        {
            foreach (var cmd in cmds.EnumerateArray())
                ds.ControlCommands.Add(ParseControlCommandRule(cmd));
        }

        return ds;
    }

    private static TrdpControlCommandRule ParseControlCommandRule(JsonElement el)
    {
        var rule = new TrdpControlCommandRule
        {
            Id            = el.TryGetProperty("id",             out var id)   ? id.GetString() ?? ""          : "",
            Description   = el.TryGetProperty("description",    out var desc) ? desc.GetString() ?? ""        : "",
            Trigger       = el.TryGetProperty("trigger",        out var trg)  ? trg.GetString() ?? "rising_edge" : "rising_edge",
            SrcVar        = el.TryGetProperty("src_var",        out var sv)   ? sv.GetString() ?? ""          : "",
            Bit           = el.TryGetProperty("bit",            out var b)    ? b.GetInt32()                  : null,
            InvertBit     = el.TryGetProperty("invert_bit",     out var ib)   && ib.GetBoolean(),
            Condition     = el.TryGetProperty("condition",      out var c)    ? c.GetString()                 : null,
            LogicOp       = el.TryGetProperty("logic_op",       out var lo)   ? lo.GetString() ?? "AND"       : "AND",
            MinIntervalMs = el.TryGetProperty("min_interval_ms",out var mi)   ? mi.GetInt32()                 : 1000,
            ActionType    = el.TryGetProperty("action_type",    out var at)   ? at.GetString() ?? "send_scmd" : "send_scmd"
        };

        if (el.TryGetProperty("value", out var v))
            rule.Value = v.GetDouble();

        if (el.TryGetProperty("value_list", out var vl) && vl.ValueKind == JsonValueKind.Array)
            rule.ValueList = vl.EnumerateArray().Select(x => x.GetDouble()).ToList();

        if (el.TryGetProperty("control_conditions", out var cconds) && cconds.ValueKind == JsonValueKind.Array)
        {
            foreach (var cc in cconds.EnumerateArray())
                rule.ControlConditions.Add(ParseControlCondition(cc));
        }

        if (el.TryGetProperty("conditions", out var conds) && conds.ValueKind == JsonValueKind.Array)
        {
            foreach (var cc in conds.EnumerateArray())
                rule.Conditions.Add(ParseControlCondition(cc));
        }

        if (el.TryGetProperty("command", out var cmdEl))
            rule.Command = ParseScmdAction(cmdEl);

        return rule;
    }

    private static TrdpControlCondition ParseControlCondition(JsonElement cc)
    {
        return new TrdpControlCondition
        {
            ControlVar = cc.TryGetProperty("control_var", out var cv) ? cv.GetString() ?? "" : "",
            Condition  = cc.TryGetProperty("condition",   out var c)  ? c.GetString() ?? "equals" : "equals",
            Value      = cc.TryGetProperty("value",       out var v)  ? v.GetDouble() : 0
        };
    }

    private static TrdpScmdAction ParseScmdAction(JsonElement cmd)
    {
        return new TrdpScmdAction
        {
            FrameType     = cmd.TryGetProperty("frame_type",     out var ft)  ? ft.GetString() ?? "0x7A" : "0x7A",
            SubCmd        = cmd.TryGetProperty("sub_cmd",        out var sc)  ? sc.GetString() ?? "0x0005" : "0x0005",
            Data          = cmd.TryGetProperty("data",           out var d)   ? d.GetDouble() : 0,
            DataFromVar   = cmd.TryGetProperty("data_from_var",  out var dfv) ? dfv.GetString() ?? "" : "",
            Use16BitData  = cmd.TryGetProperty("use_16bit_data", out var u16) && u16.GetBoolean(),
            TimeVar       = cmd.TryGetProperty("time_var",       out var tv)  ? tv.GetString() : null
        };
    }

    private static TrdpDatasetVariable ParseDatasetVariable(JsonElement v)
    {
        return new TrdpDatasetVariable
        {
            Id          = v.TryGetProperty("id",          out var id)  ? id.GetString() ?? ""     : "",
            Offset      = v.TryGetProperty("offset",      out var off) ? off.GetInt32()            : 0,
            Type        = v.TryGetProperty("type",        out var t)   ? t.GetString() ?? "uint8"  : "uint8",
            Size        = v.TryGetProperty("size",        out var sz)  ? sz.GetInt32()             : null,
            Scale       = v.TryGetProperty("scale",       out var sc)  ? sc.GetDouble()            : null,
            OffsetVal   = v.TryGetProperty("offset_val",  out var ov)  ? ov.GetDouble()            : null,
            Endian      = v.TryGetProperty("endian",      out var en)  ? en.GetString()            : null,
            Unit        = v.TryGetProperty("unit",        out var u)   ? u.GetString()             : null,
            ReverseBits = v.TryGetProperty("reverse_bits",out var rb)  && rb.GetBoolean(),
            Description = v.TryGetProperty("description", out var ds)  ? ds.GetString() ?? ""      : ""
        };
    }

    private static TrdpVariableLink ParseVariableLink(JsonElement lnk)
    {
        var link = new TrdpVariableLink
        {
            Type           = lnk.TryGetProperty("type",               out var t)   ? t.GetString() ?? "analog"  : "analog",
            SourceControlVar = lnk.TryGetProperty("source_control_var",out var scv) ? scv.GetString()            : null,
            SourceBit      = lnk.TryGetProperty("source_bit",         out var sb)  ? sb.GetInt32()              : null,
            TargetCommsVar = lnk.TryGetProperty("target_comms_var",   out var tcv) ? tcv.GetString() ?? ""      : "",
            TargetBit      = lnk.TryGetProperty("target_bit",         out var tb)  ? tb.GetInt32()              : null,
            Scale          = lnk.TryGetProperty("scale",              out var sc)  ? sc.GetDouble()             : null,
            Offset         = lnk.TryGetProperty("offset",             out var of)  ? of.GetDouble()             : null,
            SetOnly        = lnk.TryGetProperty("set_only",           out var so)  && so.GetBoolean(),
            Condition      = lnk.TryGetProperty("condition",          out var cond)? cond.GetString()           : null,
            Comment        = lnk.TryGetProperty("comment",            out var cm)  ? cm.GetString()             : null,
            Length         = lnk.TryGetProperty("length",             out var len) ? len.GetInt32()             : null,
        };

        if (lnk.TryGetProperty("value", out var valEl))
            link.Value = valEl.GetDouble();

        if (lnk.TryGetProperty("values", out var valsEl) && valsEl.ValueKind == JsonValueKind.Array)
            link.Values = valsEl.EnumerateArray().Select(x => x.GetDouble()).ToList();

        // logic_to_bit
        if (lnk.TryGetProperty("operator", out var opEl))
            link.Operator = opEl.GetString();

        if (lnk.TryGetProperty("inputs", out var inputsEl) && inputsEl.ValueKind == JsonValueKind.Array)
        {
            link.Inputs = new List<TrdpLogicInput>();
            foreach (var inp in inputsEl.EnumerateArray())
            {
                var input = new TrdpLogicInput
                {
                    SourceControlVar = inp.TryGetProperty("source_control_var", out var isrc) ? isrc.GetString() ?? "" : "",
                    SourceBit        = inp.TryGetProperty("source_bit",         out var isb)  ? isb.GetInt32()         : null,
                    Condition        = inp.TryGetProperty("condition",           out var ic)   ? ic.GetString()         : null,
                };
                if (inp.TryGetProperty("value", out var iv))  input.Value  = iv.GetDouble();
                if (inp.TryGetProperty("values", out var ivs) && ivs.ValueKind == JsonValueKind.Array)
                    input.Values = ivs.EnumerateArray().Select(x => x.GetDouble()).ToList();
                link.Inputs.Add(input);
            }
        }

        return link;
    }

    private static TrdpNetwork ParseNetwork(JsonElement net)
    {
        var network = new TrdpNetwork
        {
            ReceivedIp = net.TryGetProperty("received_ip", out var rip) ? rip.GetString() ?? "" : "",
            Name       = net.TryGetProperty("name",        out var n)   ? n.GetString() ?? ""   : ""
        };

        if (net.TryGetProperty("publishers", out var pubs))
        {
            foreach (var p in pubs.EnumerateArray())
                network.Publishers.Add(new TrdpPublisher
                {
                    Name     = p.TryGetProperty("name",     out var pn)  ? pn.GetString() ?? ""  : "",
                    Ip       = p.TryGetProperty("ip",       out var pip) ? pip.GetString() ?? "" : "",
                    ComId    = p.TryGetProperty("com_id",   out var ci)  ? ci.GetInt64()          : 0,
                    Dataset  = p.TryGetProperty("dataset",  out var pd)  ? pd.GetString() ?? ""  : "",
                    Interval = p.TryGetProperty("interval", out var pi)  ? pi.GetInt32()          : 0
                });
        }

        if (net.TryGetProperty("subscribers", out var subs))
        {
            foreach (var s in subs.EnumerateArray())
                network.Subscribers.Add(new TrdpSubscriber
                {
                    Name    = s.TryGetProperty("name",    out var sn)  ? sn.GetString() ?? ""  : "",
                    Ip      = s.TryGetProperty("ip",      out var sip) ? sip.GetString() ?? "" : "",
                    ComId   = s.TryGetProperty("com_id",  out var sci) ? sci.GetInt64()         : 0,
                    Dataset = s.TryGetProperty("dataset", out var sd)  ? sd.GetString() ?? ""  : ""
                });
        }

        return network;
    }
}
