using ApsMonitor.Models;
using Microsoft.EntityFrameworkCore;

namespace ApsMonitor.Data;

public class ApsDbContext : DbContext
{
    public ApsDbContext(DbContextOptions<ApsDbContext> options) : base(options) { }

    public DbSet<Signal> Signals { get; set; }
    public DbSet<Window> Windows { get; set; }
    public DbSet<EventMessage> Events { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure primitive collections for SQLite using JSON serialization
        modelBuilder.Entity<Signal>()
            .Property(e => e.BitTextoActivo)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<string?[]>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new string?[16]);

        modelBuilder.Entity<Signal>()
            .Property(e => e.BitTextoInactivo)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<string?[]>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new string?[16]);
        
        // Seed some initial data here so the UI looks like the screenshots immediately
        modelBuilder.Entity<Window>().HasData(
            new Window { Id = 1, Nombre = "Panel Principal APS", Descripcion = "Panel de control principal del sistema APS", Categoria = "Control", Tipo = "Normal", IsActive = true },
            new Window { Id = 2, Nombre = "Sinóptico Eléctrico APS", Descripcion = "Diagrama eléctrico simplificado del sistema APS", Categoria = "Sinópticos", Tipo = "Sinóptico", IsActive = true }
        );

        modelBuilder.Entity<Signal>().HasData(
            new Signal { Id = 1, Nombre = "Tensión Batería", Tag = "V_BAT", TipoVariable = "UINT16", BytePosicion = 0, Escala = 0.1, Offset = 0, ValorActual = 100.91, Unidad = "V", Formato = "0.01", NodoNumero = 1, NodoDescripcion = "Nodo de Control" },
            new Signal { Id = 2, Nombre = "Corriente Carga", Tag = "I_CARGA", TipoVariable = "UINT16", BytePosicion = 2, Escala = 0.1, Offset = 0, ValorActual = 23.12, Unidad = "A", Formato = "0.01", NodoNumero = 1, NodoDescripcion = "Nodo de Control" },
            new Signal { Id = 3, Nombre = "Temp. Transformador", Tag = "T_TRANS", TipoVariable = "UINT16", BytePosicion = 4, Escala = 0.1, Offset = -40, ValorActual = 26.39, Unidad = "°C", Formato = "0.01", NodoNumero = 1, NodoDescripcion = "Nodo de Control" },
            new Signal { Id = 4, Nombre = "Estado Inversor", Tag = "EST_INV", TipoVariable = "UINT8", BytePosicion = 6, Escala = 1, Offset = 0, ValorActual = 1, Unidad = "", Formato = "0", NodoNumero = 2, NodoDescripcion = "Nodo de Potencia" },
            new Signal { Id = 5, Nombre = "Estado Rectificador", Tag = "EST_RECT", TipoVariable = "UINT8", BytePosicion = 7, Escala = 1, Offset = 0, ValorActual = 0, Unidad = "", Formato = "0", NodoNumero = 2, NodoDescripcion = "Nodo de Potencia" },
            new Signal { Id = 6, Nombre = "Alarma Temperatura", Tag = "ALARM_TEMP", TipoVariable = "UINT8", BytePosicion = 8, Escala = 1, Offset = 0, ValorActual = 1, Unidad = "", Formato = "0", NodoNumero = 2, NodoDescripcion = "Nodo de Potencia" },
            new Signal { Id = 7, Nombre = "Potencia Salida", Tag = "P_SALIDA", TipoVariable = "UINT16", BytePosicion = 9, Escala = 0.01, Offset = 0, ValorActual = 42.9, Unidad = "kW", Formato = "0.01", NodoNumero = 2, NodoDescripcion = "Nodo de Potencia" },
            new Signal { Id = 8, Nombre = "Frecuencia Red", Tag = "F_RED", TipoVariable = "UINT16", BytePosicion = 11, Escala = 0.01, Offset = 0, ValorActual = 26.81, Unidad = "Hz", Formato = "0.01", NodoNumero = 1, NodoDescripcion = "Nodo de Control" }
        );

        modelBuilder.Entity<EventMessage>().HasData(
            new EventMessage { Id = 1, Mensaje = "Arrancar Inversor", Fecha = new DateTime(2023, 3, 20, 12, 13, 14), Estado = "OK" }
        );
    }
}
