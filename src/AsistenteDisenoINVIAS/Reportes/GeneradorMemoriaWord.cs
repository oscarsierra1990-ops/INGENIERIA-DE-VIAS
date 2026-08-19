using System;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using AsistenteDisenoINVIAS.Modelo;

namespace AsistenteDisenoINVIAS.Reportes
{
    /// <summary>
    /// Genera la memoria descriptiva de diseño geométrico (.docx) a partir del
    /// <see cref="ResultadoDiseno"/> acumulado durante el uso de las Pestañas 1
    /// a 3. No requiere Microsoft Word instalado: usa Open XML SDK directamente
    /// sobre el paquete .docx.
    /// </summary>
    public static class GeneradorMemoriaWord
    {
        public static void Generar(string rutaArchivo, ResultadoDiseno resultado)
        {
            using WordprocessingDocument doc = WordprocessingDocument.Create(rutaArchivo, WordprocessingDocumentType.Document);
            MainDocumentPart mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            Body body = mainPart.Document.AppendChild(new Body());

            AgregarPortada(body, resultado);
            AgregarParametrosEntrada(body, resultado.Parametros);
            AgregarSeccionPlanta(body, resultado);
            AgregarSeccionPerfil(body, resultado);
            AgregarSeccionTransversal(body, resultado);
            AgregarElementosNativos(body, resultado);
            AgregarAdvertencias(body, resultado);
            AgregarConclusiones(body, resultado);

            body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1134, Bottom = 1134, Left = 1417, Right = 1417 }));

            mainPart.Document.Save();
        }

        // ---------------------------------------------------------------
        // Portada
        // ---------------------------------------------------------------
        private static void AgregarPortada(Body body, ResultadoDiseno resultado)
        {
            body.Append(Titulo("MEMORIA DESCRIPTIVA DE DISEÑO GEOMÉTRICO", 20, true));
            body.Append(Titulo("Manual de Diseño Geométrico de Carreteras - INVIAS", 12, true, negrita: false, italica: true));
            body.Append(ParrafoVacio());

            var datos = new List<(string, string)>
            {
                ("Alineamiento:", string.IsNullOrEmpty(resultado.Parametros.NombreAlineamiento) ? "Sin procesar" : resultado.Parametros.NombreAlineamiento),
                ("Fecha de generación:", DateTime.Now.ToString("dd/MM/yyyy HH:mm")),
                ("Categoría de la vía:", resultado.Parametros.CategoriaVia),
                ("Tipo de terreno:", resultado.Parametros.TipoTerreno),
                ("Velocidad específica Vtr:", $"{resultado.Parametros.VelocidadDiseno:F0} km/h"),
            };
            foreach (var (etiqueta, valor) in datos)
                body.Append(ParrafoEtiqueta(etiqueta, valor));

            body.Append(ParrafoVacio());
            body.Append(Parrafo(
                "Este documento resume los parámetros de entrada adoptados y los resultados del cálculo automático de " +
                "planta, perfil, sobreanchos y peraltes generado por el Asistente de Diseño Vial INVIAS para Civil 3D, " +
                "junto con la justificación normativa de cada decisión de diseño."));
            body.Append(SaltoDePagina());
        }

        // ---------------------------------------------------------------
        // 1. Parámetros de entrada
        // ---------------------------------------------------------------
        private static void AgregarParametrosEntrada(Body body, ParametrosEntrada p)
        {
            body.Append(Titulo("1. Parámetros de Entrada", 16, false));
            body.Append(Parrafo(
                "Los siguientes parámetros fueron seleccionados por el diseñador en el asistente y constituyen la base " +
                "de todos los cálculos normativos que se documentan a continuación."));

            var filas = new List<string[]>
            {
                new[] { "Categoría de la vía", p.CategoriaVia },
                new[] { "Tipo de terreno", p.TipoTerreno },
                new[] { "Velocidad específica de diseño (Vtr)", $"{p.VelocidadDiseno:F0} km/h" },
                new[] { "Pendiente longitudinal máxima admisible", $"{p.PendienteMaximaAdmisible:F1} %" },
                new[] { "Radio mínimo admisible en planta", $"{p.RadioMinimoAdmisible:F1} m" },
                new[] { "Longitud mínima de tangente vertical", $"{p.LongitudMinimaTangenteVertical:F1} m" },
                new[] { "Coeficiente K mínimo (curva cresta)", $"{p.KMinimoCresta:F1}" },
                new[] { "Coeficiente K mínimo (curva columpio)", $"{p.KMinimoColumpio:F1}" },
                new[] { "Superficie de terreno natural (MDT)", string.IsNullOrEmpty(p.NombreSuperficie) ? "-" : p.NombreSuperficie },
                new[] { "Ancho de carril", $"{p.AnchoCarril:F2} m" },
                new[] { "Vehículo de diseño", p.VehiculoDiseno },
                new[] { "Longitud del vehículo de diseño", $"{p.LongitudVehiculo:F1} m" },
                new[] { "Fricción transversal máxima admisible", $"{p.FriccionTransversalMaxima:F2}" },
            };
            body.Append(CrearTabla(new[] { "Parámetro", "Valor adoptado" }, filas));

            body.Append(Parrafo(
                $"La velocidad específica Vtr = {p.VelocidadDiseno:F0} km/h se adoptó a partir del cruce entre la categoría " +
                $"de vía \"{p.CategoriaVia}\" y el tipo de terreno \"{p.TipoTerreno}\", conforme a los rangos de velocidad " +
                "recomendados por el Manual de Diseño Geométrico de Carreteras de INVIAS. A partir de esta velocidad se " +
                "derivan todos los umbrales normativos (pendiente máxima, radio mínimo, coeficientes K, sobreancho y peralte) " +
                "utilizados en el resto del documento."));
        }

        // ---------------------------------------------------------------
        // 2. Planta
        // ---------------------------------------------------------------
        private static void AgregarSeccionPlanta(Body body, ResultadoDiseno resultado)
        {
            body.Append(Titulo("2. Diseño Geométrico en Planta", 16, false));

            if (!resultado.PlantaProcesada || resultado.CurvasHorizontales.Count == 0)
            {
                body.Append(Parrafo("No se procesó el alineamiento horizontal en esta sesión, o el eje no contiene curvas circulares."));
                return;
            }

            var p = resultado.Parametros;
            body.Append(Parrafo(
                $"El eje '{p.NombreAlineamiento}' se generó a partir de la polilínea de eje seleccionada por el diseñador, entre " +
                $"las abscisas {FormatearAbscisa(p.AbscisaInicial)} y {FormatearAbscisa(p.AbscisaFinal)}. Para Vtr = {p.VelocidadDiseno:F0} km/h " +
                $"el radio mínimo normativo es Rmin = {p.RadioMinimoAdmisible:F1} m. El trazado contiene {resultado.CurvasHorizontales.Count} curva(s) circular(es); " +
                "los radios de cada curva corresponden a la geometría dibujada por el diseñador y NO fueron alterados por el asistente, que se limita a validarlos " +
                "contra el mínimo normativo."));

            var filas = resultado.CurvasHorizontales.Select(c => new[]
            {
                c.Elemento,
                FormatearAbscisa(c.AbscisaInicio),
                FormatearAbscisa(c.AbscisaFin),
                c.Radio.ToString("F2"),
                c.DeltaGrados.ToString("F2") + "°",
                c.Longitud.ToString("F2"),
                c.GiraDerecha ? "Derecha" : "Izquierda",
                c.CumpleRadioMinimo ? "Cumple" : "NO CUMPLE"
            }).ToList();

            body.Append(CrearTabla(new[] { "Curva", "Abs. Inicio", "Abs. Fin", "Radio (m)", "Delta", "Longitud (m)", "Sentido", "Rmin" }, filas));

            int noCumplen = resultado.CurvasHorizontales.Count(c => !c.CumpleRadioMinimo);
            if (noCumplen > 0)
            {
                body.Append(Parrafo(
                    $"ADVERTENCIA: {noCumplen} curva(s) presentan un radio inferior al mínimo normativo Rmin = {p.RadioMinimoAdmisible:F1} m para la velocidad " +
                    "específica adoptada. Se recomienda ampliar el radio, reducir la velocidad específica de ese tramo o justificar la condición restrictiva " +
                    "conforme a los criterios del Manual INVIAS para casos excepcionales.", negrita: true));
            }
            else
            {
                body.Append(Parrafo("Todas las curvas circulares del alineamiento cumplen el radio mínimo normativo para la velocidad específica adoptada."));
            }
        }

        // ---------------------------------------------------------------
        // 3. Perfil / Rasante
        // ---------------------------------------------------------------
        private static void AgregarSeccionPerfil(Body body, ResultadoDiseno resultado)
        {
            body.Append(Titulo("3. Diseño Geométrico en Perfil (Rasante)", 16, false));

            if (!resultado.PerfilProcesado || resultado.CurvasVerticales.Count == 0)
            {
                body.Append(Parrafo("No se procesó el perfil longitudinal en esta sesión."));
                return;
            }

            var p = resultado.Parametros;
            body.Append(Parrafo(
                $"La rasante se calculó automáticamente sobre la superficie de terreno natural '{p.NombreSuperficie}', respetando una pendiente " +
                $"longitudinal máxima de {p.PendienteMaximaAdmisible:F1} % y una entretangencia mínima de {p.LongitudMinimaTangenteVertical:F1} m entre " +
                "vértices (PVI). En cada quiebre de pendiente con diferencia algebraica A ≥ 0.5 % se calculó una curva vertical parabólica cuya longitud " +
                "adopta el mayor valor entre el criterio de comodidad (Lv = K·A), el criterio de visibilidad de parada (AASHTO) y la longitud mínima " +
                "visual (0.6·Vtr), limitada por la entretangencia disponible entre vértices consecutivos."));

            var filas = resultado.CurvasVerticales.Where(c => c.Tipo != "N/A").Select(c => new[]
            {
                c.Elemento,
                FormatearAbscisa(c.Abscisa),
                c.Cota.ToString("F2"),
                c.PendienteEntrada.ToString("F2"),
                c.PendienteSalida.ToString("F2"),
                c.DiferenciaAlgebraica.ToString("F2"),
                c.Tipo,
                c.KAplicado.ToString("F1"),
                c.LongitudCurva.ToString("F1"),
                c.CumpleLongitudMinima ? "Cumple" : "Limitada"
            }).ToList();

            if (filas.Count == 0)
            {
                body.Append(Parrafo("El perfil no presentó quiebres de pendiente que requirieran curva vertical (terreno prácticamente uniforme)."));
                return;
            }

            body.Append(CrearTabla(new[] { "PVI", "Abscisa", "Cota (m)", "g1 (%)", "g2 (%)", "A (%)", "Tipo", "K aplicado", "Lv (m)", "Estado" }, filas));

            int limitadas = filas.Count(f => f[9] == "Limitada");
            if (limitadas > 0)
            {
                body.Append(Parrafo(
                    $"OBSERVACIÓN: {limitadas} curva(s) vertical(es) quedaron con una longitud menor a la requerida por comodidad y/o visibilidad de parada, " +
                    "debido a la entretangencia disponible entre PVIs consecutivos. Se recomienda revisar el espaciamiento de vértices en esos tramos.", negrita: true));
            }
        }

        // ---------------------------------------------------------------
        // 4. Sección transversal: sobreancho y peralte
        // ---------------------------------------------------------------
        private static void AgregarSeccionTransversal(Body body, ResultadoDiseno resultado)
        {
            body.Append(Titulo("4. Sección Transversal: Sobreanchos, Peraltes y Bordes de Vía", 16, false));

            if (!resultado.TransversalProcesado || resultado.CurvasHorizontales.All(c => c.SobreanchoMaximo <= 0 && c.PeralteMaximo <= 0))
            {
                body.Append(Parrafo("No se procesó la sección transversal (sobreanchos/peraltes) en esta sesión."));
                return;
            }

            var p = resultado.Parametros;
            body.Append(Parrafo(
                $"Con ancho de carril de {p.AnchoCarril:F2} m y vehículo de diseño \"{p.VehiculoDiseno}\" (longitud {p.LongitudVehiculo:F1} m), se calculó para cada " +
                "curva circular el sobreancho de calzada mediante la fórmula oficial INVIAS para pavimento de dos carriles " +
                "S = 2·(R−√(R²−L²)) + Vtr/(10·√R), y el peralte máximo asociado al radio de la curva. La longitud de transición se distribuye en la proporción " +
                "2/3 sobre la tangente recta de acceso y 1/3 dentro de la curva circular, evitando escalones de transición de longitud nula. Los bordes de vía " +
                "se generaron como alineamientos de desfase (offset) nativos de Civil 3D a ± ancho de carril, con el sobreancho aplicado como ensanchamiento " +
                "variable (Widening) en cada curva, y los peraltes se inyectaron directamente en las curvas de peralte nativas del alineamiento."));

            var filas = resultado.CurvasHorizontales
                .Where(c => c.SobreanchoMaximo > 0 || c.PeralteMaximo > 0)
                .Select(c => new[]
                {
                    c.Elemento,
                    c.Radio.ToString("F2"),
                    c.SobreanchoMaximo.ToString("F2"),
                    c.PeralteMaximo.ToString("F2"),
                    c.LongitudTransicion.ToString("F2"),
                    c.GiraDerecha ? "Derecha" : "Izquierda"
                }).ToList();

            body.Append(CrearTabla(new[] { "Curva", "Radio (m)", "Sobreancho S (m)", "Peralte e (%)", "Long. Transición Lt (m)", "Sentido" }, filas));
        }

        // ---------------------------------------------------------------
        // 5. Elementos nativos generados
        // ---------------------------------------------------------------
        private static void AgregarElementosNativos(Body body, ResultadoDiseno resultado)
        {
            body.Append(Titulo("5. Elementos Nativos Generados en Civil 3D", 16, false));
            if (resultado.ElementosNativosGenerados.Count == 0)
            {
                body.Append(Parrafo("No se registraron elementos nativos generados en esta sesión."));
                return;
            }
            body.Append(Parrafo("Todos los resultados descritos en este documento quedaron representados como objetos nativos e inteligentes de Civil 3D " +
                "(no como líneas o bloques estáticos), de forma que permanecen asociativos ante cambios posteriores del eje, la rasante o la superficie:"));
            foreach (var item in resultado.ElementosNativosGenerados)
                body.Append(ParrafoVinieta(item));
        }

        // ---------------------------------------------------------------
        // 6. Advertencias
        // ---------------------------------------------------------------
        private static void AgregarAdvertencias(Body body, ResultadoDiseno resultado)
        {
            body.Append(Titulo("6. Advertencias y Observaciones", 16, false));
            if (resultado.Advertencias.Count == 0)
            {
                body.Append(Parrafo("No se registraron incumplimientos normativos ni condiciones restrictivas durante el proceso de diseño."));
                return;
            }
            foreach (var item in resultado.Advertencias)
                body.Append(ParrafoVinieta(item));
        }

        // ---------------------------------------------------------------
        // 7. Conclusiones y salvedades
        // ---------------------------------------------------------------
        private static void AgregarConclusiones(Body body, ResultadoDiseno resultado)
        {
            body.Append(Titulo("7. Conclusiones y Salvedades", 16, false));
            body.Append(Parrafo(
                "El diseño geométrico documentado en esta memoria fue generado de forma automática por el Asistente de Diseño Vial INVIAS a partir de los " +
                "parámetros de entrada indicados en la Sección 1, aplicando las fórmulas y tablas usuales del Manual de Diseño Geométrico de Carreteras de " +
                "INVIAS (radios mínimos y peraltes con e_max = 8 %, coeficientes K por comodidad y visibilidad de parada, fórmula oficial de sobreancho)."));
            body.Append(Parrafo(
                "SALVEDAD: los valores normativos utilizados constituyen una aproximación de ingeniería basada en las tablas habituales del Manual INVIAS. " +
                "Antes de utilizar este documento con fines contractuales, de interventoría o de aprobación ante la entidad competente, el diseñador debe " +
                "verificar cada valor contra la edición vigente del Manual aplicable al proyecto y contra las condiciones particulares del corredor " +
                "(seguridad vial, drenaje, predios, geotecnia, redes existentes, etc.), que no son evaluadas por esta herramienta.", negrita: true));
        }

        // ---------------------------------------------------------------
        // Helpers de formato Open XML
        // ---------------------------------------------------------------
        private static string FormatearAbscisa(double station)
        {
            int km = (int)(station / 1000);
            double m = station % 1000;
            return $"K{km}+{m:000.00}";
        }

        private static Paragraph Titulo(string texto, int tamanoPt, bool centrado, bool negrita = true, bool italica = false)
        {
            var runProps = new RunProperties();
            if (negrita) runProps.Append(new Bold());
            if (italica) runProps.Append(new Italic());
            runProps.Append(new FontSize { Val = (tamanoPt * 2).ToString() });

            var run = new Run(runProps, new Text(texto));
            var paragraph = new Paragraph(run);
            var pProps = new ParagraphProperties(new SpacingBetweenLines { Before = "240", After = "120" });
            if (centrado) pProps.Append(new Justification { Val = JustificationValues.Center });
            paragraph.PrependChild(pProps);
            return paragraph;
        }

        private static Paragraph Parrafo(string texto, bool negrita = false)
        {
            var runProps = new RunProperties();
            if (negrita) runProps.Append(new Bold());
            var run = new Run(runProps, new Text(texto) { Space = SpaceProcessingModeValues.Preserve });
            var paragraph = new Paragraph(run);
            paragraph.PrependChild(new ParagraphProperties(new SpacingBetweenLines { After = "160" }));
            return paragraph;
        }

        private static Paragraph ParrafoEtiqueta(string etiqueta, string valor)
        {
            var runEtiqueta = new Run(new RunProperties(new Bold()), new Text(etiqueta + " ") { Space = SpaceProcessingModeValues.Preserve });
            var runValor = new Run(new Text(valor));
            return new Paragraph(runEtiqueta, runValor);
        }

        private static Paragraph ParrafoVinieta(string texto)
        {
            var run = new Run(new Text("• " + texto) { Space = SpaceProcessingModeValues.Preserve });
            var paragraph = new Paragraph(run);
            paragraph.PrependChild(new ParagraphProperties(new SpacingBetweenLines { After = "80" }, new Indentation { Left = "284" }));
            return paragraph;
        }

        private static Paragraph ParrafoVacio() => new Paragraph(new Run(new Text("")));

        private static Paragraph SaltoDePagina() => new Paragraph(new Run(new Break { Type = BreakValues.Page }));

        private static Table CrearTabla(string[] encabezados, List<string[]> filas)
        {
            var tabla = new Table();
            var props = new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 6 },
                    new BottomBorder { Val = BorderValues.Single, Size = 6 },
                    new LeftBorder { Val = BorderValues.Single, Size = 6 },
                    new RightBorder { Val = BorderValues.Single, Size = 6 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
                new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" });
            tabla.AppendChild(props);

            var filaEncabezado = new TableRow();
            foreach (var h in encabezados)
                filaEncabezado.Append(CrearCelda(h, negrita: true));
            tabla.Append(filaEncabezado);

            foreach (var fila in filas)
            {
                var tr = new TableRow();
                foreach (var celda in fila)
                    tr.Append(CrearCelda(celda, negrita: false));
                tabla.Append(tr);
            }

            return tabla;
        }

        private static TableCell CrearCelda(string texto, bool negrita)
        {
            var runProps = new RunProperties();
            if (negrita) runProps.Append(new Bold());
            runProps.Append(new FontSize { Val = "18" });
            var run = new Run(runProps, new Text(texto ?? "") { Space = SpaceProcessingModeValues.Preserve });
            var paragraph = new Paragraph(run);
            var celda = new TableCell(paragraph);
            celda.Append(new TableCellProperties(new TableWidth { Type = TableWidthUnitValues.Auto }));
            return celda;
        }
    }
}
