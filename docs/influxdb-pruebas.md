# Monitor SEPSA → InfluxDB 2.7 → Grafana

Esta integración corresponde a `/monitor` (MonitorStateService / SEPSA).
No exporta sesiones importadas ni captura TRDP. Solo escribe las señales decodificadas
de cada respuesta del bucle continuo; el botón de trama de prueba no exporta datos.

## Preparación

1. En http://localhost:8086, abrir Load Data → Buckets → Create Bucket.
   Crear `aps_monitor_pruebas`, con retención de 7 días para limitar el histórico de pruebas.
2. Anotar el nombre de la organización (menú de organización/perfil).
3. En Load Data → API Tokens generar un Custom API Token con escritura únicamente
   sobre ese bucket. Grafana necesita permiso de lectura del nuevo bucket en su propio token.
4. En `appsettings.json`, sección `InfluxDB`, poner Organization y Enabled=true.
   Bucket ya contiene `aps_monitor_pruebas`. No guardar el token en este archivo.
5. Proporcionar el token al proceso servidor como variable de entorno `InfluxDB__Token`.
   Por ejemplo, desde PowerShell, en la carpeta del proyecto:

   ```powershell
   $influxSecret = Read-Host 'Token de escritura InfluxDB' -AsSecureString
   $env:InfluxDB__Token = [System.Net.NetworkCredential]::new('', $influxSecret).Password
   dotnet run --no-launch-profile
   Remove-Item Env:InfluxDB__Token
   ```

   Desde Visual Studio: clic derecho en el proyecto ApsMonitor → Administrar secretos
   de usuario. En el archivo secrets.json añadir `"InfluxDB:Token": "TOKEN_REAL"`
   dentro del objeto JSON, conservando cualquier otro secreto existente. El proyecto
   tiene UserSecretsId y los perfiles http/https usan Development, por lo que se carga
   automáticamente. Reiniciar la aplicación al cambiar la configuración.
6. Conectar al equipo y empezar la monitorización. Comprobar en la pantalla que aumentan
   las muestras confirmadas y aparece la última escritura correcta.

`localhost:8086` sirve si el servidor .NET está en el mismo ordenador que Docker y el
contenedor publica 8086. Si .NET también está en Docker, usar el nombre del servicio
InfluxDB en una red compartida. No desactivar validación TLS para destinos HTTPS.

## Datos y Grafana

Medición: `aps_monitor`. Etiquetas: `equipo`, `Sesion`, `nodo`, `signal_id`, `senal`.
Campo numérico: `valor`. Timestamp UTC de recepción/procesamiento, con precisión ns;
no es un timestamp proporcionado por el equipo. Cada inicio genera una sesión distinta.
La identificación del nodo mantiene la lógica existente del monitor (tamaño de trama).

Consulta Flux de ejemplo para un panel Time series usando la fuente InfluxDB existente:

```flux
from(bucket: "aps_monitor_pruebas")
  |> range(start: v.timeRangeStart, stop: v.timeRangeStop)
  |> filter(fn: (r) => r._measurement == "aps_monitor" and r._field == "valor")
  |> aggregateWindow(every: v.windowPeriod, fn: mean, createEmpty: false)
  |> yield(name: "mean")
```

Elegir últimos 5 minutos y refresco 5s. Para estados digitales conviene usar `last`
en lugar de `mean`. Añadir filtros de `senal` o `Sesion` para limitar las series.
Las gráficas de sesiones CSV existentes pueden necesitar esta consulta nueva.

## Límites de la versión básica

- Configuración de pruebas actual: activada, organización `mi-org`, bucket
  `aps_monitor_pruebas`. Sin un token válido se muestra un error y no se encolan muestras.
- Envío cada segundo, hasta 5000 muestras por lote; ambos límites son configurables.
- Cola en memoria de 100000 muestras más un lote en curso. Cuando se llena se descartan
  las muestras nuevas y aumenta el contador visible. Valores no finitos o etiquetas
  con caracteres de control también se descartan.
- Timeout HTTP 5s. Fallos de red, HTTP 408/429 y 5xx conservan el lote para reintentar.
  Otros errores HTTP descartan el lote y muestran el código; revisar token, bucket y permisos.
  Una escritura parcial puede haber guardado parte del lote aunque figure como no confirmado.
- Detener la monitorización permite vaciar la cola mientras la aplicación sigue abierta.
  Antes de cerrar, esperar Pendientes=0. Cerrar/reiniciar pierde las muestras pendientes.
- No hay cola persistente ni garantía de entrega durante apagados en esta versión.

## Comprobación manual

Con el equipo disponible: iniciar captura, comparar varias señales y fechas con Grafana,
detener captura y esperar Pendientes=0. Interrumpir temporalmente InfluxDB durante una
captura: el monitor debe seguir leyendo, mostrar error y acumular pendientes. Recuperar
InfluxDB y comprobar que aumentan confirmadas y se vacía la cola. Iniciar otra captura
y comprobar que utiliza otra etiqueta Sesion. No se ha ejecutado esta prueba real sin
organización, token y equipo disponibles.
