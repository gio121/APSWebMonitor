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
2. Se puede seleccionar la dirección IP del equipo destino tanto en el panel superior de **Reprogramación** como dentro del diálogo **Cambiar fichero…** (permitiendo elegir entre la IP configurada del monitor, concesiones activas de equipos en el servidor DHCP o introducir una IP personalizada). La reprogramación de comunicaciones utiliza el puerto por defecto del protocolo SFTP (puerto 22). Firma, bloque y desplazamiento se asignan según el software seleccionado.
3. Para control/FPGA: cargar BIN o Intel HEX (`.hex`, `.h86`, `.mcs`) sin cifrar. BIN se utiliza literalmente; en HEX se aplica el desplazamiento del software y se rellenan huecos con FF.
4. Para comunicaciones: se configura el **Usuario SFTP** (por defecto `sepsa`), la **Contraseña** (`thisisveryunsafepleasedeactivatemeaftersetup`) y la **Ruta remota destino** (por defecto `/mnt/artifacts/update`). La aplicación se conecta directamente por SFTP (puerto 22) sin requerir el demonio UDP de SEPSA en el puerto 50000 (evitando errores de "Port Unreachable" en equipos Linux/buildroot), y asegura la creación del directorio remoto si no existe. Se pueden seleccionar imágenes o paquetes `.img` y `.gz` (con sus `.md5` correspondientes si se incluyen), o una pareja `edf` y `sdf` (`.bin` o `.gz`). Se comprueban los MD5 antes de iniciar la transferencia.
5. Pulsar **Guardar selección** para volver a la tabla; el nombre del fichero, la IP destino, el usuario y la ruta remota quedan visibles en su fila. Puede volver a **Cambiar fichero…** para sustituirlos antes de cargarlos. Pulsar **Reprogramar**, revisar la confirmación (que detalla IP, puerto, usuario, ruta remota y archivos) e iniciar. El monitor detiene y espera las peticiones activas antes de transmitir. Solo se permite una reprogramación global. La operación continúa al navegar a otra pantalla; volver a Reprogramación muestra su estado. Debe mantenerse el servidor en ejecución.
6. El éxito requiere respuesta explícita del equipo o confirmación de transferencia SFTP al 100%. La monitorización se reanuda manualmente después de comprobar el resultado y, en comunicaciones, de que termine un posible reinicio.

No se ofrece interrupción de una escritura flash: cerrar la página no cancela la operación. Si hay un error o expira el plazo, el estado indica que no se ha confirmado el resultado; no se comunica un éxito por haber transferido el fichero.

## Configuración del servidor

Los parámetros de conexión se establecen en el diálogo o mediante configuración ASP.NET (`appsettings.json` o variables de entorno):
- `Reprogramming:SftpPort` (22 por defecto).
- `Reprogramming:SftpUser` (`sepsa` por defecto).
- `Reprogramming:SftpPassword` (`thisisveryunsafepleasedeactivatemeaftersetup` por defecto).
- `Reprogramming:SftpRemotePath` (`/mnt/artifacts/update` por defecto).
- `Reprogramming:UdpPort` (50000 por defecto para control/FPGA y modo legado SEPSA).
- `Reprogramming:LocalPort` (0 por defecto, automático).
- `Reprogramming:HostKeySha256` (opcional).

## Protocolo

- Control/FPGA: inicio 4A/4B; solicitudes 4C/4D; bloques 44/45, firma ASCII de 6 bytes, índice LE, datos y CRC16 XMODEM BE por UDP (puerto 50000). Checksum SEPSA aditivo. El equipo decide qué bloque solicitar.
- Comunicaciones (SFTP directo): conexión directa al puerto 22 mediante SFTP con las credenciales indicadas (`sepsa`), subiendo los archivos a la ruta remota configurada (`/mnt/artifacts/update`) con monitorización de porcentaje en tiempo real y creación automática de directorios remotos.
- Comunicaciones (Modo legado MetroMadrid): si no se especifica usuario, comando UDP 8B/0001 en puerto 50000 para obtener credenciales dinámicas; respuesta 82/83; subida SFTP; comando 8B/0004 y consulta de porcentaje hasta 100.
- Límites: imagen control/FPGA 16 MiB, máximo 32767 bloques; comunicaciones 32 archivos y 512 MiB en total. Las cargas se mantienen en memoria, no se publican como archivos estáticos.

## Verificación

```powershell
dotnet build ApsMonitor.csproj
dotnet run --project tests/ReprogrammingChecks
```

Las comprobaciones cubren CRC conocidos, tramas, validación de HEX/MD5, bloques y repeticiones mediante UDP loopback, estados de error, confirmación final y coordinación SEPSA/SFTP con un transportador SFTP simulado. No sustituyen la validación en banco: no se ha reprogramado ningún equipo real ni se ha probado una sesión SSH contra el LDTS.

SFTP utiliza [SSH.NET](https://github.com/sshnet/SSH.NET/releases), versión 2026.0.0.
