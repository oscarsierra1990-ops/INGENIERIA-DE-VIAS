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
   El alineamiento nativo se crea respetando el radio real dibujado; cada
   curva se valida contra el radio mínimo normativo (ya no se sobrescribe
   la geometría, a diferencia de la versión anterior).
2. **Pestaña 2 — Perfil & Rasante**: elija la superficie de terreno natural
   (MDT) y procese. Se genera el perfil TN, la rasante calculada
   automáticamente (pendiente máxima, entretangencia, curvas verticales por
   comodidad K y por visibilidad de parada) y la vista de perfil, junto con
   un cuadro de PVIs.
3. **Pestaña 3 — Transversales**: defina ancho de carril y vehículo de
   diseño, y procese. Se calculan sobreancho y peralte por curva con la
   fórmula oficial INVIAS, se generan los alineamientos de borde de vía
   (desfase ±ancho de carril con ensanchamiento variable) y se inyectan los
   peraltes nativamente en el alineamiento. El botón opcional "Generar
   Corredor Nativo" intenta además construir un `Corridor` con `Assembly`
   real (best-effort: depende del catálogo de subensambles instalado; si
   falla, los bordes de vía siguen disponibles como alineamientos nativos).
4. **Pestaña 4 — Memoria**: genera un `.docx` con los parámetros de entrada,
   los resultados de cada pestaña y la justificación normativa de las
   decisiones adoptadas, incluyendo advertencias de incumplimiento si las
   hay.

## Cambios relevantes frente a la versión original

- **Corrección de bug crítico en Planta**: ya no se fuerza
  `arc.Radius = radioMinimo` en cada curva (eso destruía el trazado
  dibujado por el usuario). Ahora se valida el radio real contra el mínimo
  normativo y se reporta cualquier incumplimiento.
- **Corrección en Transversales**: el emparejamiento entre curvas de
  peralte nativas (`SuperelevationCurve`) y curvas geométricas del eje ya
  no depende de la posición en la lista (que podía desincronizarse), sino
  de la estación real de cada curva.
- **Normativa centralizada** en `InviasNormativa.cs`, con interpolación
  entre quiebres de tabla y nuevas verificaciones (distancia de visibilidad
  de parada) que antes no existían.
- **Memoria descriptiva en Word**, con parámetros de entrada, resultados y
  justificación de planta/perfil/transversal, generada con Open XML SDK
  (no requiere Word instalado).
- **Corredor nativo opcional** como complemento a los bordes de vía ya
  generados, con localización de subensambles por reflexión para tolerar
  variaciones del catálogo instalado sin romper la compilación.

## Salvedad normativa

Los valores de radio mínimo, peralte máximo, coeficientes K y distancia de
visibilidad de parada reproducen las tablas usuales del Manual INVIAS
(aproximación AASHTO, e_max = 8 %). Verifique estos valores contra la
edición vigente del Manual antes de usar los resultados con fines
contractuales o de aprobación ante la entidad competente.
