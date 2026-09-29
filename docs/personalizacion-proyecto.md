# Imagen y nombre del proyecto

En **Administración → Imagen y nombre del proyecto**, escribe el nombre y selecciona una imagen PNG, JPG o WebP de hasta 2 MiB. El nombre admite hasta 80 caracteres.

La vista previa permite revisar el resultado antes de pulsar **Guardar personalización**. Al guardar, la cabecera superior izquierda de los monitores abiertos se actualiza. **Descartar cambios** recupera la configuración guardada. Para recuperar el logo original, pulsa **Usar imagen predeterminada** y guarda.

La configuración se conserva en `aps.db`, incluida la imagen; no depende del fichero original ni de una ruta en el ordenador del administrador. La tabla se crea automáticamente en instalaciones existentes. Solo los administradores tienen acceso al panel de edición.

Las comprobaciones de `tests/ProjectBrandingChecks` usan una base SQLite temporal para verificar la actualización del esquema, la persistencia, la notificación de cambios y el rechazo de entradas no válidas.
