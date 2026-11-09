# Visualización del sinóptico: zoom y centrado

Actualizado el 5 de noviembre de 2026. Estado: implementado según la simplificación solicitada.

La barra del visor y del editor se sitúa abajo a la derecha del contenedor y contiene **−, porcentaje editable, + y Centrar**. Ocupa un bloque compacto bajo el lienzo, dejando accesible todo el esquema. El porcentaje permite valores del 10 al 400 % y se aplica al confirmar con Enter o salir del campo. El botón Centrar coloca el centro del esquema en el centro del área visible, conservando el porcentaje elegido.

Al abrir una ventana se realiza un encuadre inicial automático. Después de elegir un porcentaje, ese tamaño se conserva al cambiar el espacio disponible o actualizar la telemetría. La altura del área de trabajo aprovecha automáticamente el espacio restante de la pantalla.

Se han retirado los botones Ajustar todo, Ajustar ancho, 100 %, Expandir/Restaurar y el tirador de altura. Para volver a la escala original basta con escribir 100 en el porcentaje.

El desplazamiento sigue disponible con las barras internas, arrastrando el fondo del visor o con espacio + arrastre en el editor. Control + rueda amplía sobre el cursor y dos dedos permiten ampliar en dispositivos táctiles. Las coordenadas guardadas de los elementos se mantienen en unidades originales; el zoom y el centrado no generan cambios pendientes en el proyecto.

## Espacio superior

`MainLayout` no contiene una barra `MudAppBar`, pero los estilos de `MudMainContent` reservaban su altura. Se sustituye esa reserva por un margen superior de 16 px y se elimina el relleno superior adicional de `monitor-page-content`. Así la cabecera, las tarjetas y el esquema aprovechan la altura recuperada y mantienen una pequeña separación con el borde de la pantalla. El botón para abrir el menú lateral sigue visible cuando corresponde.

## Archivos principales

- `Components/Shared/SynopticViewport.razor` y sus estilos aislados: barra y área desplazable compartidas.
- `wwwroot/js/synoptic-viewport.js`: porcentaje, centrado, encuadre inicial, coordenadas e interacción.
- `Components/Pages/SinopticoViewer.razor` y `SinopticoEditor.razor`: integración y edición en coordenadas originales.
- `Components/Layout/MainLayout.razor`: distribución adaptable y eliminación del espacio superior reservado.

## Verificación

Las pruebas se encuentran en `tests/SynopticViewportChecks`. Incluyen geometría con rotaciones y coordenadas negativas, precisión de arrastre y redimensionado a cinco escalas, porcentaje editable y límites, confirmación con Enter, centrado conservando el zoom, gestos táctiles y conservación de la configuración.

El navegador aislado comprueba siete resoluciones, de 360×800 a 2560×1440. La página de prueba utiliza el componente real de Blazor, los estilos reales de MudBlazor y el CSS de MainLayout. Se comprueba expresamente el margen superior de 16 px y que solo aparecen los tres botones previstos. También se verifica con una copia de la geometría de FLP, sin escribir en la base del proyecto.

Las capturas locales están en `artifacts/synoptic-viewport-checks` y `artifacts/synoptic-flp-viewport`. Estas comprobaciones no sustituyen la revisión del flujo completo de una sesión autenticada o una prueba con un equipo físico.
