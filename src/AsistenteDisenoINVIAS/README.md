# Asistente de Diseño Vial INVIAS — Civil 3D 2025

Complemento (.NET 8 / WPF) para AutoCAD Civil 3D 2025 que automatiza el
diseño geométrico de una vía conforme al Manual de Diseño Geométrico de
Carreteras de INVIAS: alineamiento horizontal, rasante, sobreanchos,
peraltes, bordes de vía y una memoria descriptiva en Word.

## Estructura del proyecto

```
AsistenteDisenoINVIAS.csproj
Comandos.cs                     Comando INVIAS_DISENO que abre el panel
MainWindow.xaml / .xaml.cs      Panel con las 4 pestañas y orquestación
Modelo/                         ParametrosEntrada, DatosCurvaHorizontal,
                                 DatosCurvaVertical, ResultadoDiseno
Normativa/InviasNormativa.cs    Tablas y fórmulas INVIAS centralizadas
Utilidades/GeometriaHelper.cs   Formato de abscisas/ángulos, sentido de giro
Servicios/
  ServicioPlanta.cs             Pestaña 1: alineamiento horizontal
  ServicioPerfil.cs             Pestaña 2: rasante y perfil
  ServicioTransversal.cs        Pestaña 3: sobreancho, peralte, bordes de vía
  ServicioCorredor.cs           Corredor nativo opcional (Assembly/Corridor)
Reportes/GeneradorMemoriaWord.cs Memoria descriptiva en .docx (Open XML SDK)
```

## Compilación

Requisitos:

- Visual Studio 2022 (o posterior) con soporte .NET 8 y WPF.
- AutoCAD Civil 3D 2025 instalado (para las referencias en
  `AsistenteDisenoINVIAS.csproj`, que apuntan por defecto a
  `C:\Program Files\Autodesk\AutoCAD 2025\...`; ajústelas si su instalación
  usa otra ruta).
- Conexión a NuGet para restaurar `DocumentFormat.OpenXml` (usado para
  generar el .docx sin depender de Microsoft Word instalado).

Pasos:

1. Abra `AsistenteDisenoINVIAS.csproj` en Visual Studio (o ejecute
   `dotnet build` desde una máquina Windows con las referencias arriba
   disponibles).
2. Compile en `x64`.
3. Cargue el `.dll` resultante en Civil 3D con `NETLOAD`.
4. Ejecute el comando `INVIAS_DISENO` para abrir el panel.

> Este entorno de desarrollo no tiene AutoCAD/Civil 3D ni Windows
> disponibles, por lo que el código no pudo compilarse ni probarse aquí.
> Revíselo en Visual Studio antes de usarlo en producción.

## Uso

1. **Pestaña 1 — Planta**: seleccione categoría de vía, tipo de terreno y
   Vtr (o déjelo autocompletar), seleccione la polilínea de eje y procese.
   El asistente PROPONE el trazado: si una curva no cumple el radio mínimo
   normativo, amplía automáticamente su radio hasta ese mínimo (conservando
   el resto de los PI/tangentes dibujados); solo deja la curva como
   advertencia para rediseño manual si la geometría del eje no tiene
   tangente suficiente para ampliarla.
2. **Pestaña 2 — Perfil & Rasante**: elija la superficie de terreno natural
   (MDT) y procese. La rasante se PROPONE automáticamente: cada curva
   vertical se dimensiona siempre con la longitud completa que exige el
   criterio más estricto (comodidad K·A, visibilidad de parada AASHTO o
   longitud mínima visual) — nunca se recorta una curva para que "quepa".
   Si un quiebre del terreno no deja espacio para una curva conforme junto
   a la vecina, el vértice se omite y el tramo se traza recto, en vez de
   generar una curva corta no conforme.
3. **Pestaña 3 — Transversales**: defina ancho de carril y vehículo de
   diseño, y procese. Se calculan sobreancho y peralte por curva con la
   fórmula oficial INVIAS, se generan los alineamientos de borde de vía
   (desfase ±ancho de carril con ensanchamiento variable) y se inyectan los
   peraltes nativamente en el alineamiento. El botón opcional "Generar
   Corredor Nativo" intenta además construir un `Corridor` con `Assembly`
   real (best-effort: depende del catálogo de subensambles instalado; si
   falla, los bordes de vía siguen disponibles como alineamientos nativos).
4. **Pestaña 4 — Memoria**: genera un `.docx` con los parámetros de entrada,
   los resultados de cada pestaña, la Sección 5 "Decisiones de Diseño
   Adoptadas" (cada ajuste automático de planta/perfil con su justificación
   normativa) y las advertencias residuales que sí requieren intervención
   manual del diseñador.

## Cambios relevantes frente a la versión original

- **De verificador a asistente propositivo**: la versión anterior de este
  mismo desarrollo (post-corrección de bug) solo validaba el trazado y
  reportaba incumplimientos. Ahora, cuando el trazado no cumple, el
  asistente ajusta automáticamente la geometría (radio en planta, longitud
  y ubicación de curvas verticales en perfil) hasta lograr un diseño
  conforme, y documenta cada ajuste como una "decisión de diseño" con su
  justificación normativa en la memoria. Solo se deja como advertencia lo
  que realmente no se puede resolver sin rediseñar manualmente el eje.
- **Corrección del bug original en Planta**: la primera versión forzaba
  `arc.Radius = radioMinimo` en TODAS las curvas sin condición, destruyendo
  cualquier radio mayor dibujado a propósito. Ahora solo se interviene (se
  amplía) la curva que efectivamente incumple; el resto del trazado
  dibujado se conserva intacto.
- **Corrección en Transversales**: el emparejamiento entre curvas de
  peralte nativas (`SuperelevationCurve`) y curvas geométricas del eje ya
  no depende de la posición en la lista (que podía desincronizarse), sino
  de la estación real de cada curva.
- **Normativa centralizada** en `InviasNormativa.cs`, con interpolación
  entre quiebres de tabla y nuevas verificaciones (distancia de visibilidad
  de parada) que antes no existían.
- **Memoria descriptiva en Word**, con parámetros de entrada, resultados y
  justificación de planta/perfil/transversal/decisiones adoptadas, generada
  con Open XML SDK (no requiere Word instalado).
- **Corredor nativo opcional** como complemento a los bordes de vía ya
  generados, con localización de subensambles por reflexión para tolerar
  variaciones del catálogo instalado sin romper la compilación.

## Salvedad normativa

Los valores de radio mínimo, peralte máximo, coeficientes K y distancia de
visibilidad de parada reproducen las tablas usuales del Manual INVIAS
(aproximación AASHTO, e_max = 8 %). Verifique estos valores contra la
edición vigente del Manual antes de usar los resultados con fines
contractuales o de aprobación ante la entidad competente.
