# Plan para versionar las revisiones de proyectos

Fecha: 4 de noviembre de 2026. Estado: propuesta de implementación; las bases de proyectos todavía no se incorporan al repositorio.

## Objetivo y situación actual

El catálogo publicado debe ser único y verificable. Una revisión identificada por proyecto y versión debe contener exactamente la misma configuración en todas las instalaciones. Los usuarios de una misma instancia ya comparten el proyecto activo y el borrador; incorporar archivos a Git por sí solo no sincroniza otras instalaciones ni obliga a que todas ejecuten la misma revisión.

Actualmente el monitor obtiene las revisiones de `Projects:RootDirectory`, cuyo valor local es `C:\SEPSA\ApsWebMonitor`. Estos archivos están fuera del repositorio del código. La publicación crea una carpeta inmutable en `Revisions`, pero no hace un commit ni distribuye esa revisión a otros servidores.

El inventario local contiene seis revisiones publicadas, con aproximadamente 5,14 MiB de bases SQLite:

| Proyecto | Revisiones |
| --- | --- |
| FLP | 0.1, 0.2, 0.3 |
| MediaDistancia | 0.1, 0.3 |
| MetroMadrid | 0.1 |

Las seis bases también contienen filas de usuarios, eventos y permisos de instalación. El importador del catálogo debe generar bases nuevas de configuración, en lugar de subir esas bases completas o limitarse a borrar sus filas. Los borradores pendientes se conservan y no se convierten automáticamente en revisiones publicadas.

## Diseño propuesto

Empezar con un catálogo en este mismo repositorio, bajo `ProjectRevisions/`. Así un commit identifica tanto el código como el catálogo que se entrega. Con el tamaño actual puede iniciarse con Git ordinario; el crecimiento del historial determinará si conviene trasladar los binarios a LFS o a artefactos de publicación.

```text
ProjectRevisions/
  catalog.json
  FLP/Revisions/0.1/
    configuration.json
    aps.db
    revision.json
  FLP/Revisions/0.2/
    configuration.json
    aps.db
    revision.json
```

`configuration.json` será una exportación determinista que permita revisar diferencias. `aps.db` será una base nueva, generada desde esa configuración y validada, que permita cargar el proyecto con el modelo actual. Ambos archivos se generarán juntos; no se editarán manualmente ni se combinarán bases binarias durante un merge.

La exportación incluirá `Signals`, `Windows`, `Commands`, `TrdpConfigs`, `ProjectBranding` e `ImportedControlEventConfigurations`. Mantendrá sus identificadores, enlaces, textos de proyecto y reglas de acceso propias de las ventanas. Excluirá cuentas, contraseñas, permisos globales de instalación, eventos de ejecución y valores en vivo. Los roles referenciados por una ventana se validarán contra la instalación, sin sustituirlos ni conceder permisos automáticamente.

`revision.json` registrará proyecto, versión, revisión de origen, versión de esquema, autor y fecha conocidos, descripción y hashes SHA-256 de los archivos y de la configuración lógica. No se inventarán autores ni fechas para las revisiones antiguas que carecen de metadatos. `catalog.json` enumerará todas las revisiones oficiales y la revisión aprobada por proyecto. El commit del repositorio identifica la entrega completa; su SHA se mostrará en la aplicación, sin introducir una autorreferencia al propio commit dentro del manifiesto.

La huella lógica debe ser independiente del idioma, de las fechas de modificación y de la disposición física de SQLite. Los nombres internos de tablas, claves y propiedades no se traducen.

## Fase 1: inventario y exportación inicial

1. Hacer una copia coherente y verificable de las revisiones y borradores actuales mediante el mecanismo de backup de SQLite, incluyendo los datos pendientes del WAL. Conservarla fuera del catálogo publicado.
2. Obtener el inventario de las demás instalaciones y comparar cada pareja proyecto/versión mediante la huella lógica. No asumir que la copia local es la más reciente.
3. Si una misma versión tiene contenidos distintos, conservar ambas variantes y resolver cuál es la oficial con el responsable del proyecto. Una variante diferente recibirá una nueva revisión válida; no se sobrescribirá una existente ni se mezclarán SQLite automáticamente.
4. Implementar un exportador que ordene tablas, filas y propiedades, conserve los IDs y utilice formatos invariantes. Generar las bases de configuración nuevas a partir de la exportación.
5. Importar las seis revisiones locales y las adicionales que aparezcan en el inventario, comprobar los enlaces entre señales, ventanas, sinópticos, comandos y eventos, y revisar el primer commit del catálogo.

Resultado: todas las revisiones históricas identificadas están conservadas y el catálogo inicial se reproduce desde un checkout limpio, sin datos de cuentas o ejecución.

## Fase 2: validación del catálogo en Git

Añadir comprobaciones de CI para integridad SQLite, esquema admitido, unicidad de proyecto/versión, correspondencia entre JSON y base, referencias válidas y coincidencia de hashes. Los cambios en el catálogo deberán pasar estas comprobaciones antes de entrar en `master`.

Una revisión que ya existe en la rama oficial será inmutable: se rechazan su modificación y eliminación. Cualquier corrección se publicará con una versión posterior. El catálogo deberá listar todos los archivos esperados y rechazar revisiones incompletas, duplicadas o con contenido no permitido.

Excluir explícitamente de este catálogo `.Working`, `.Runtime`, `.uploads`, archivos de selección activa, bloqueos, logs, certificados privados y archivos auxiliares de SQLite. Añadir comprobaciones que impidan incorporarlos accidentalmente.

Resultado: un cambio de código o una publicación posterior no puede alterar silenciosamente una revisión ya distribuida.

## Fase 3: publicación desde el monitor

Introducir un servicio de publicación sobre `ProjectLoadingService.PublishAsync` y `ProjectDatabaseService.PublishDraftAsync`:

1. Capturar y validar el borrador bajo los bloqueos existentes.
2. Consultar el catálogo remoto y comprobar que la versión está libre y el origen sigue siendo válido.
3. Generar JSON, base y manifiesto en una carpeta temporal.
4. Crear un commit y una solicitud de integración con la identidad técnica del servidor y el autor funcional en los metadatos. Mantener la publicación como pendiente hasta su incorporación a la rama oficial.
5. Cuando las comprobaciones y la integración terminen, sincronizar el catálogo, registrar el commit publicado y abrir el borrador de la nueva revisión.

Mantener una operación pendiente recuperable, con ID, hashes y estado, para los reinicios y errores de red. Reintentar la misma publicación será idempotente. Si otro usuario ocupa esa versión, se propone otra; no se fuerza el push ni se sobrescribe su contenido. Si Git no está disponible, se conserva el borrador y se informa de que la revisión aún no está compartida.

Las credenciales de Git pertenecerán al servidor y no se guardarán en los proyectos ni se distribuirán entre usuarios. La autorización funcional seguirá usando los permisos de publicación del monitor.

## Fase 4: distribución y revisión activa

Crear un servicio de catálogo que descargue un commit oficial completo en una zona temporal, verifique sus hashes y active la nueva copia mediante un cambio atómico. No ejecutar un pull sobre bases que el monitor esté usando ni reemplazar una revisión local con contenido diferente.

La copia del catálogo será de solo lectura para el monitor; las escrituras continuarán en los borradores y en `.Runtime`, fuera del checkout. Una actualización no descarta borradores ni cambia el proyecto durante una transferencia al equipo.

La pantalla y la cabecera mostrarán proyecto, revisión, commit del catálogo y estado de sincronización. Dentro de una instancia seguirá existiendo una sola selección activa compartida. Entre instalaciones, la revisión operativa será la aprobada por el catálogo central; abrir una revisión histórica o un borrador será una acción explícita y claramente identificada. Esto evita confundir «tener el mismo catálogo» con «estar usando la misma versión».

Si una instalación está desconectada, podrá conservar la última entrega verificada con su estado visible. No se presentará una publicación local pendiente como una revisión oficial compartida.

## Fase 5: pruebas y despliegue

- Dos usuarios de una instancia ven la misma revisión activa y el mismo borrador.
- Dos instalaciones que cargan la misma revisión obtienen los mismos hashes, independientemente del idioma.
- Dos publicaciones concurrentes de la misma versión conservan ambos trabajos y solo una ocupa esa versión.
- Se conservan todas las revisiones anteriores y sus vínculos al actualizar el esquema de un borrador.
- Un corte de red o reinicio antes o después del push no pierde cambios ni duplica revisiones.
- Un archivo alterado, una revisión incompleta o un catálogo incompatible se rechaza antes de activarse.
- El catálogo exportado no contiene cuentas ni registros de ejecución.

Hacer el despliegue primero en un entorno de prueba con copias de los tres proyectos, después en una instalación piloto y finalmente en el resto. Conservar las copias previas y permitir volver al commit anterior del catálogo sin borrar los borradores posteriores.

## Orden recomendado de implementación

Primero el exportador/importador y el catálogo inicial; después la validación en CI y la distribución de solo lectura; finalmente la publicación integrada desde el monitor y la coordinación de instalaciones. Hasta completar la migración, la carpeta actual sigue siendo el origen de los proyectos y no se añaden sus bases completas al repositorio.
