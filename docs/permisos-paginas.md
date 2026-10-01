# Roles y acceso a pestañas

En Administración, la tarjeta **Roles y acceso a pestañas** permite seleccionar un rol y activar o desactivar sus secciones del menú lateral. Cada interruptor guarda el cambio inmediatamente en `aps.db` y afecta a todos los usuarios del rol seleccionado.

Para crear un rol, escribe su nombre y pulsa **Crear rol**. Empieza sin pestañas habilitadas; selecciona las que necesite. Después, en **Gestionar usuarios**, crea o edita un usuario y elige el nuevo rol. Un usuario conectado al que se le cambie el rol debe volver a iniciar sesión para adoptar ese rol.

El mismo permiso controla el menú y la apertura mediante URL. Los cambios de permisos se aplican a los usuarios conectados; retirar el acceso a la página abierta muestra un aviso de acceso denegado. Administración y el editor de sinópticos siguen reservados al administrador, que mantiene acceso completo.

Se conservan los permisos ya configurados para Mantenimiento. Sin configuración previa, este rol tiene Inicio, Ventanas, Sesiones y Señales. Los permisos de cada ventana sinóptica son independientes y siguen vigentes dentro de Ventanas.

Las comprobaciones de `tests/PageAccessChecks` verifican persistencia, concesión y retirada de permisos, separación de roles y políticas de las rutas utilizando una base de datos temporal.
