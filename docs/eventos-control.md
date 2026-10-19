# Eventos de control

Disponible en **Eventos de control** (`/eventos-control`). El administrador tiene acceso; los demás roles requieren habilitar esta página en Administración.

## Uso

1. Abrir **Eventos de control**. La pantalla utiliza automáticamente las señales añadidas en **Señales**; no tiene selector de CMFX ni exige cargar de nuevo el fichero. **Actualizar señales** vuelve a leer los datos guardados si se han editado en otra pestaña.
2. Abrir un DAT binario o seleccionar un nodo y pulsar **Descargar del APS**. La descarga usa la IP y el puerto UDP configurados en el monitor. Se pausa la monitorización durante la transferencia y se reanuda si estaba activa y el destino sigue siendo el mismo. Cancelar o salir de la página interrumpe la descarga.
3. La lista muestra número de evento, fecha y fallos, empezando por el número más alto. Las posiciones vacías o inválidas se pueden mostrar con el interruptor. Las fechas BCD se interpretan como UTC y se convierten a Europe/Madrid o UTC; los filtros de fecha usan la zona seleccionada. Las fechas inválidas se muestran como `—`.
4. Pulsar un evento para consultar los valores de avería, la última muestra de cada traza y el desglose de bits. Elegir un bloque de traza y las señales o bits que se quieren representar. Las señales con distintas unidades aparecen en ejes separados. El microcontrolador usa índice de muestra; FPGA usa su reloj en microsegundos, con desbordamiento cada 256 µs.
5. **Guardar DAT** conserva los bytes originales. Los CSV exportan los eventos filtrados, el detalle filtrado del evento o las series de traza seleccionadas. Son UTF-8 con BOM, separador `;` y valores entre comillas.

Cambiar de nodo o actualizar las señales limpia el DAT anterior para evitar mezclar estructuras. Esta página interpreta DAT binarios de control; los logs de comunicaciones se consultan en su pantalla correspondiente.

## Configuración compartida con las señales

La importación de **Señales** guarda los registros de señales y la estructura de `event_trace` en una misma transacción. La tabla `ImportedControlEventConfigurations` conserva por nodo la firma, direcciones, tamaños, posiciones dentro del evento, referencias de trazas y enumeraciones de averías que no caben en los campos de monitorización de `Signals`.

Al abrir eventos se resuelven las referencias por nodo y `Tag` contra las señales activas de la base de datos. Los nombres, tipos, unidades, escalas, desplazamientos y textos de bits proceden de esas señales actuales. Se excluyen las señales eliminadas. Las posiciones `BytePosicion` de monitorización se conservan y no sustituyen las posiciones específicas de los eventos. Eliminar todas las señales deshabilita los nodos de eventos; reimportarlas recupera sus identidades y estructura. Importar un nodo sin eventos retira su estructura anterior.

Las instalaciones nuevas crean la tabla con `EnsureCreated`; las existentes la añaden mediante `ControlEventsSchema.EnsureImportedConfiguration`. Las importaciones antiguas descartaban `event_trace`. En esta instalación se ha recuperado su estructura de la configuración `C135KI` que coincide con las 287 señales del nodo 2, conservando las ediciones y todos los identificadores y vínculos existentes. Se verificó que señales, ventanas y comandos coinciden exactamente con la copia SQLite anterior a la recuperación. La aplicación no consulta el proyecto original en ejecución.

## Referencia

Proyecto consultado en lectura: `C:\REPO\Monitores\QT8802_MONMDx\trunk`.

- `MON/frmMain.cs`: apertura por firma ASCII de seis bytes y guardado del DAT.
- `MON/MainApplication.cs`: configuración de la descarga desde `event_download`.
- `MON/CommonForms/frmTabularEvents.cs`: posiciones de registros, número `failReg`, fechas BCD en bytes bajos de palabras, fallos `cfails*`, valores y última muestra de las trazas.
- `MON/CommonForms/FPGA traces/frmGraphics.cs`: orden de palabras FPGA, identificación analógica/digital/estado, validez, reloj y máscaras.
- `MON/SepsaCtlNet/SepsaCtlNet.dll`: inspección de `EventsDownloadProcess`, `SepsaProtocol`, `Conversion.Time` y `CmfxFunctions` para reproducir el formato. No hay dependencia de la DLL antigua.
- `MON/xmlconfig.cmfx`: firma `N200KI`, nodo 2, 50 eventos de 24576 bytes desde 16384; imagen de 1245184 bytes, bloques de 1024, 200 muestras de microcontrolador y 3072 registros FPGA por evento. El propio CMFX contiene referencias FPGA sin definición en `value_elements`; la página avisa de ellas y omite esas señales.

## Protocolo y límites

Petición SEPSA `5C`, bytes de subcomando `00 01` RAM o `00 02` FLASH, dirección de 32 bits y tamaño de 16 bits en little endian. Respuesta `54`: firma de seis bytes, subcomando, dirección, tamaño y datos. Se comprueban longitud, origen, destino, tipo, subcomando, dirección, tamaño y checksum aditivo antes de aceptar el bloque. Se ignoran respuestas corruptas o de otro bloque. Se reintenta cada segundo, hasta cinco intentos por bloque, con límite global de ocho minutos.

Las imágenes deben comenzar en dirección 0 y no superar 50 MiB. Los bloques de descarga no pueden superar 32767 bytes. Se verifica la firma y el tamaño del DAT contra la estructura guardada durante la importación, así como los límites de cada bloque y señal. Los tipos numéricos enteros, FLOAT32, FLOAT64, BCD_BYTE y ASCII_BYTE se interpretan en little endian, con escala y desplazamiento de las señales guardadas. Las máscaras FPGA se aplican como en el monitor original, que mantiene `mask2` a cero.

## Validación

```powershell
dotnet build ApsMonitor.csproj
dotnet run --project tests/ControlEventsChecks -- "C:\REPO\Monitores\QT8802_MONMDx\trunk\MON\xmlconfig.cmfx"
dotnet run --project tests/PageAccessChecks
dotnet run --project tests/ControlCommandChecks
dotnet run --project tests/SignalBindingChecks
node tests/ControlEventsChecks/chart-checks.cjs
```

Las pruebas usan imágenes sintéticas y UDP loopback: fechas BCD, escalas y signo, enumeraciones, bits, trazas, reloj FPGA, firmas y tamaños incorrectos, checksum, bloques repetidos, reintentos, bloque final corto, cancelación, exclusión de reprogramación y pausa/reanudación de monitorización. También comprueban persistencia de la estructura con las señales, lectura después de reiniciar el servicio, cambios de nombre/tipo/escala/unidad/bits, borrado y reimportación, transacciones fallidas y renderizado de la pantalla sin un segundo selector de CMFX. El argumento CMFX es opcional y permite comprobar la geometría real del proyecto de referencia. Falta la validación en banco con un DAT descargado de un equipo real.
