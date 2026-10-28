# Carga de proyectos

Todos los usuarios autenticados pueden abrir **Cambiar proyecto** pulsando la imagen o la cabecera donde aparecen el proyecto y su versión, en la parte superior del panel lateral. Toda esa zona es un enlace, también accesible con el teclado. Esta opción no depende del rol ni de los permisos de las páginas de administración.

Selecciona primero el **Proyecto**, después su **Revisión / versión**, y pulsa **Cargar proyecto**. Las versiones aparecen de mayor a menor (por ejemplo, `0.10` antes que `0.2`). Los proyectos se buscan en `C:\SEPSA\ApsWebMonitor`, con esta estructura:

```text
C:\SEPSA\ApsWebMonitor\
  MetroMadrid\
    Revisions\
      0.1\
        aps.db
      0.2\
        aps.db
```

Todas las carpetas de proyectos aparecen en el selector, aunque estén vacías. Al elegir una carpeta sin revisiones, se muestra un aviso y puedes importar su primera base de datos. Las carpetas de versiones también aparecen aunque aún no contengan una base; **Cargar proyecto** se habilita cuando existe una base de datos en la revisión elegida. La lista se actualiza automáticamente cada dos segundos mientras esta página está abierta; también puedes pulsar **Actualizar lista**. Las actualizaciones conservan tu selección y no cambian el proyecto activo.

Puedes copiar una base de datos existente dentro de la carpeta de su revisión; se admiten extensiones `.db`, `.sqlite` y `.sqlite3`. También puedes **Importar base de datos**: indica el nombre del proyecto y la versión (por ejemplo, `MetroMadrid` y `0.1`) y selecciona un archivo de hasta 100 MiB. Se guardará como `<Proyecto>\Revisions\<Versión>\aps.db`, tras comprobar su integridad y compatibilidad. Una revisión que ya tiene base de datos no se sobrescribe: introduce otra versión. Exporta o copia la base con la aplicación de origen cerrada para incluir todos los datos pendientes de SQLite.

El monitor y la conexión al equipo son compartidos: el cambio de proyecto se aplica a todos los usuarios conectados. Se desconecta el equipo, se borran los valores anteriores y se recargan las pantallas; las sesiones de reproducción se cierran. Durante una conexión, reprogramación o descarga de eventos hay que esperar a que termine la operación. Después puedes volver a conectar el monitor con las señales del nuevo proyecto.

La recarga espera la respuesta del navegador y gestiona la cancelación si se cierra la pestaña o se pierde la conexión. Si no se puede abrir el selector automáticamente, se mantiene un enlace **Abrir selector de proyectos** para continuar manualmente.

Cada proyecto conserva sus señales, ventanas, eventos, comandos, configuración TRDP, imagen y nombre. Las cuentas, los roles y los permisos siguen perteneciendo a la instalación original (`DefaultConnection`, o `aps.db`), por lo que cargar una base de datos no cambia la identidad ni los privilegios de los usuarios.

`C:\SEPSA\ApsWebMonitor\active-project.txt` conserva la selección entre reinicios. La ruta raíz se configura en `Projects:RootDirectory` de `appsettings.json`. La aplicación necesita permisos de lectura y escritura en esta carpeta.

El catálogo contiene las carpetas de proyectos y sus revisiones existentes en esta ruta. La carpeta interna `.uploads`, usada durante las importaciones, no aparece como proyecto. El arranque no crea proyectos ni copia bases de datos desde la instalación o desde la antigua carpeta local `Projects/`. Las nuevas carpetas se crean únicamente al importar explícitamente una base de datos.

Si no hay selección guardada, o su proyecto ya no está disponible, se abre **Cambiar proyecto** para elegir uno existente. Si la carpeta está vacía o todavía no existe, se muestra un aviso; no se crea un proyecto de sustitución. La base de cuentas y permisos de la instalación no aparece como proyecto.
