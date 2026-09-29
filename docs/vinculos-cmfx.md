# Vínculos persistentes de señales CMFX

Las ventanas guardan identificadores de señales en su contenido, reglas de color,
estados, mandos y configuración de averías. Antes, borrar las señales eliminaba
sus registros y una importación posterior generaba identificadores distintos.

Ahora el borrado individual y «Borrar Todo» marcan las señales como inactivas
(`Signals.IsDeleted`), conservando su identidad y metadatos. Las consultas normales
no incluyen estas señales. El editor de ventanas sí las conserva y muestra
«CMFX no cargado» junto a la asignación.

La importación identifica cada señal por `NodoNumero` y `Tag` (keyname, sin distinguir
mayúsculas y eliminando espacios en los extremos). Actualiza los datos y reactiva
el registro existente sin cambiar su ID. El orden del XML, la posición de byte o
la descripción pueden cambiar sin perder el vínculo. Las señales ausentes de una
reimportación parcial siguen inactivas si antes se borraron. Importar repetidamente
el mismo CMFX no crea copias nuevas. Si ya había duplicados de importaciones antiguas,
se conservan sus IDs porque pueden estar asignados en diferentes ventanas.

Todo el CMFX se analiza antes de guardarse y se aplica en una transacción. No se
reescribe el contenido de las ventanas ni se borran sus asignaciones.

Al iniciar, `SignalSchema.EnsurePersistentBindings` añade la columna a las bases
existentes con valor 0. Sigue el mecanismo de actualización incremental de esquema
que utiliza la aplicación junto con `EnsureCreated`; las instalaciones nuevas ya
incluyen la columna. No se modifica la base de datos del usuario durante las pruebas.

Esta corrección conserva los vínculos desde su instalación. Si una versión anterior
ya eliminó físicamente el registro de una señal, su relación con el keyname no se
puede reconstruir de forma fiable solo a partir del ID guardado en la ventana: hace
falta una copia de la base de datos anterior o volver a asignar esa señal una vez.

Validación: `dotnet run --project tests/SignalBindingChecks`. Las pruebas usan una
base SQLite temporal y cubren actualización del esquema, borrado individual y total,
reimportación parcial y desordenada, reinicio, nodos con el mismo tag, duplicados
anteriores y conservación del JSON de todas las asignaciones de ventana.
