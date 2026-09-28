# Reprogramación

Disponible en **Reprogramación** (`/reprogramacion`) para el rol Administrador.

## Referencia localizada

Proyecto consultado únicamente en lectura: `C:\REPO\Monitores\QT8816_MONMETROMADRID\MON`.

- `frmMain.cs`: menús `mnuReprogram2_Click`, `mnuReprogramFPGA2_Click`, `mnuReprogram3_Click`; selección/decodificación en `LoadHexFile`; confirmación del nombre y CRC en `startReprogramming`; progreso en `swUpload`; comunicaciones en `UpdateSFTP`, `UploadLDTS_Events` y `FTP_UploadEnded`.
- `CommonForms/frmSWUploadSelect.cs`: selección de software por nodo.
- `MainApplication.cs`: `ConfigureSWUpload`, `StartSWUploadComms` y `LinuxFilesUploaded`.
- `SepsaCtlNet/SepsaCtlNet.dll`: inspección de `SwUploadProcess`, `SepsaProtocol`, `crypto` y `EthernetDriver`. La aplicación web no depende de esta DLL ni del proyecto antiguo.
- `xmlconfig.cmfx`: control, bloque 128 y desplazamiento `0xC00000`; FPGA, bloque 400 y desplazamiento 0.

## Uso

1. Seleccionar una fila en **Software del sistema**: control, FPGA, comunicaciones o configuración de comunicaciones. Pulsar **Cambiar fichero…** para abrir el diálogo de selección. Cada fila conserva su selección mientras permanezca en la página; cancelar el diálogo mantiene la selección anterior. La columna Versión muestra `?` porque todavía no se consulta la versión instalada del equipo.
2. El diálogo solo permite seleccionar o sustituir archivos. La IP se toma del monitor; firma, bloque y desplazamiento se asignan según el software seleccionado. No se requiere configurar la conexión para guardar una selección.
3. Para control/FPGA: cargar BIN o Intel HEX (`.hex`, `.h86`, `.mcs`) sin cifrar. BIN se utiliza literalmente; en HEX se aplica el desplazamiento del software y se rellenan huecos con FF.
4. Para comunicaciones: seleccionar imágenes `.img` junto con sus `.img.md5`, o una única pareja `edf*.bin` y `sdf*.bin`. Se comprueban los MD5 antes de contactar con el equipo.
5. Pulsar **Guardar selección** para volver a la tabla; el nombre del fichero queda visible en su fila. Puede volver a **Cambiar fichero…** para sustituirlo antes de cargarlo. Pulsar **Reprogramar**, revisar la confirmación e iniciar. El monitor detiene y espera las peticiones activas antes de transmitir. Solo se permite una reprogramación global. La operación continúa al navegar a otra pantalla; volver a Reprogramación muestra su estado. Debe mantenerse el servidor en ejecución.
6. El éxito requiere respuesta explícita del equipo. La monitorización se reanuda manualmente después de comprobar el resultado y, en comunicaciones, de que termine un posible reinicio.

No se ofrece interrupción de una escritura flash: cerrar la página no cancela la operación. Si hay un error o expira el plazo, el estado indica que no se ha confirmado el resultado; no se comunica un éxito por haber transferido el fichero.

## Configuración del servidor

Los parámetros de conexión se establecen fuera del diálogo, mediante configuración ASP.NET (`appsettings` o variables de entorno): `Reprogramming:UdpPort`, `Reprogramming:LocalPort` (0 por defecto), `Reprogramming:SftpPort` (22 por defecto) y `Reprogramming:HostKeySha256`. El puerto UDP debe ser el SEPSA del equipo, no el HTTP del backend. Sin puerto válido o huella SSH para SFTP, se puede seleccionar el fichero pero no realizar la transferencia.

## Protocolo

- Control/FPGA: inicio 4A/4B; solicitudes 4C/4D; bloques 44/45, firma ASCII de 6 bytes, índice LE, datos y CRC16 XMODEM BE. Checksum SEPSA aditivo, sin el ajuste `+2` del cliente HTTP existente. El equipo decide qué bloque solicitar, incluidos repetidos y el bloque terminal FF.
- Se espera 10 s antes de repetir una transmisión. Una vez anunciado el borrado/grabación, no se retransmite ni reinicia la escritura. Límites del proceso heredados: control 35 min y FPGA 12 min.
- Comunicaciones: comando 8B/0001 para obtener acceso SFTP; respuesta 82/83, decodificación de credenciales compatible con MetroMadrid; carga SFTP; comando 8B/0004 y consulta de porcentaje hasta 100. Nunca se envía el fin de transferencia si SFTP falla. Inicio: 30 s; operación global: 30 min; verificación: 5 min.
- Límites: imagen control/FPGA 16 MiB, máximo 32767 bloques; comunicaciones 32 archivos y 512 MiB en total. Las cargas se mantienen en memoria, no se publican como archivos estáticos.

## Verificación

```powershell
dotnet build ApsMonitor.csproj
dotnet run --project tests/ReprogrammingChecks
```

Las comprobaciones cubren CRC conocidos, tramas, validación de HEX/MD5, bloques y repeticiones mediante UDP loopback, estados de error, confirmación final y coordinación SEPSA/SFTP con un transportador SFTP simulado. No sustituyen la validación en banco: no se ha reprogramado ningún equipo real ni se ha probado una sesión SSH contra el LDTS.

SFTP utiliza [SSH.NET](https://github.com/sshnet/SSH.NET/releases), versión 2026.0.0.
