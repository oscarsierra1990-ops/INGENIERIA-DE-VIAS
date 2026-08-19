using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AsistenteDisenoINVIAS.Modelo;
using AsistenteDisenoINVIAS.Normativa;
using AsistenteDisenoINVIAS.Utilidades;
using Table = Autodesk.AutoCAD.DatabaseServices.Table;

namespace AsistenteDisenoINVIAS.Servicios
{
    /// <summary>
    /// Pestaña 1: crea el alineamiento horizontal nativo de Civil 3D a partir de
    /// la polilínea de eje seleccionada por el usuario.
    ///
    /// CORRECCIÓN respecto a la versión original: ya NO se sobrescribe el radio
    /// de cada arco con el radio mínimo normativo. Forzar `arc.Radius = minRadius`
    /// destruía la geometría real dibujada por el diseñador (todas las curvas
    /// terminaban con el mismo radio mínimo, sin importar el trazado). Ahora se
    /// respeta el radio dibujado y únicamente se valida contra el mínimo
    /// admisible, dejando la evidencia en <see cref="ResultadoDiseno.Advertencias"/>
    /// y en el cuadro de alineamiento.
    /// </summary>
    public static class ServicioPlanta
    {
        public static Alignment? ProcesarPlanta(
            Transaction tr,
            CivilDocument civilDoc,
            Database db,
            ObjectId polylineId,
            ParametrosEntrada parametros,
            ResultadoDiseno resultado)
        {
            Polyline? pline = tr.GetObject(polylineId, OpenMode.ForRead) as Polyline;
            if (pline == null) return null;

            double minRadius = InviasNormativa.RadioMinimo(parametros.VelocidadDiseno);
            parametros.RadioMinimoAdmisible = minRadius;
            parametros.PendienteMaximaAdmisible = InviasNormativa.PendienteMaxima(parametros.CategoriaViaIdx, parametros.VelocidadDiseno);
            parametros.LongitudMinimaTangenteVertical = InviasNormativa.LongitudMinimaTangente(parametros.VelocidadDiseno);
            parametros.KMinimoCresta = InviasNormativa.KMinimoCresta(parametros.VelocidadDiseno);
            parametros.KMinimoColumpio = InviasNormativa.KMinimoColumpio(parametros.VelocidadDiseno);
            parametros.FriccionTransversalMaxima = InviasNormativa.FriccionTransversalMaxima(parametros.VelocidadDiseno);

            ObjectId styleId = civilDoc.Styles.AlignmentStyles.Count > 0 ? civilDoc.Styles.AlignmentStyles[0] : ObjectId.Null;
            ObjectId labelSetId = civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles.Count > 0 ? civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles[0] : ObjectId.Null;

            PolylineOptions options = new PolylineOptions { PlineId = polylineId, AddCurvesBetweenTangents = true, EraseExistingEntities = false };
            string alignmentName = "Eje_INVIAS_" + DateTime.Now.ToString("HHmmss");

            ObjectId alignId = Alignment.Create(civilDoc, options, alignmentName, ObjectId.Null, db.Clayer, styleId, labelSetId);
            Alignment? alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment;
            if (alignment == null) return null;

            resultado.CurvasHorizontales.Clear();
            int arcIdx = 1;
            foreach (AlignmentEntity entity in alignment.Entities)
            {
                if (entity is AlignmentArc arc)
                {
                    var dato = new DatosCurvaHorizontal
                    {
                        Elemento = $"C-{arcIdx++}",
                        AbscisaInicio = arc.StartStation,
                        AbscisaFin = arc.EndStation,
                        Radio = arc.Radius,
                        DeltaGrados = arc.Delta * (180.0 / Math.PI),
                        Longitud = arc.Length,
                        GiraDerecha = GeometriaHelper.EsGiroDerecha(arc),
                        RadioMinimoNormativo = minRadius,
                        CumpleRadioMinimo = arc.Radius >= minRadius - 0.01
                    };

                    if (!dato.CumpleRadioMinimo)
                    {
                        dato.Observaciones = $"Radio {arc.Radius:F1} m inferior al mínimo normativo de {minRadius:F1} m para Vtr={parametros.VelocidadDiseno:F0} km/h. Verifique el trazado o reduzca la velocidad específica del tramo.";
                        resultado.RegistrarAdvertencia($"{dato.Elemento} (Planta): {dato.Observaciones}");
                    }

                    resultado.CurvasHorizontales.Add(dato);
                }
            }

            parametros.NombreAlineamiento = alignment.Name;
            parametros.AbscisaInicial = alignment.StartingStation;
            parametros.AbscisaFinal = alignment.EndingStation;

            resultado.RegistrarElementoNativo($"Alineamiento horizontal nativo '{alignment.Name}' ({alignment.Entities.Count} elementos, {alignment.Length:F2} m, Abscisas {GeometriaHelper.FormatearAbscisa(alignment.StartingStation)} a {GeometriaHelper.FormatearAbscisa(alignment.EndingStation)}).");
            resultado.PlantaProcesada = true;

            return alignment;
        }

        public static void CrearTablaAlineamientoCompleto(Database db, Transaction tr, Alignment alignment, Point3d insertPt, List<DatosCurvaHorizontal> curvasHorizontales)
        {
            if (alignment.Entities.Count == 0) return;
            Table tbl = new Table();
            tbl.SetDatabaseDefaults();
            tbl.Position = insertPt;
            tbl.SetSize(alignment.Entities.Count + 2, 11);
            tbl.Cells[0, 0].TextString = "CUADRO DE ELEMENTOS DEL ALINEAMIENTO HORIZONTAL (INVIAS)";
            tbl.Cells[0, 0].TextHeight = 3.0;
            tbl.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            string[] headers = { "Elemento", "Abs. Inicio", "Abs. Fin", "Norte Inicio", "Este Inicio", "Norte Fin", "Este Fin", "Azimut / Delta", "Radio (m)", "Longitud (m)", "Rmin OK" };
            for (int col = 0; col < headers.Length; col++)
            {
                tbl.Cells[1, col].TextString = headers[col];
                tbl.Cells[1, col].TextHeight = 2.0;
                tbl.Cells[1, col].Alignment = CellAlignment.MiddleCenter;
                tbl.Columns[col].Width = 26.0;
            }

            int lineIdx = 1, arcIdx = 1;
            for (int i = 0; i < alignment.Entities.Count; i++)
            {
                AlignmentEntity entity = alignment.Entities[i];
                int row = i + 2;
                Point2d startPt = Point2d.Origin, endPt = Point2d.Origin;
                double startSt = 0, endSt = 0, length = 0;
                string elemTag = "", paramAngulo = "", radioStr = "-", cumpleStr = "-";

                if (entity is AlignmentLine line)
                {
                    elemTag = $"T-{lineIdx++}";
                    startPt = line.StartPoint; endPt = line.EndPoint; startSt = line.StartStation; endSt = line.EndStation; length = line.Length;
                    double dE = endPt.X - startPt.X, dN = endPt.Y - startPt.Y;
                    double azRad = Math.Atan2(dE, dN);
                    if (azRad < 0) azRad += 2.0 * Math.PI;
                    paramAngulo = "Az: " + GeometriaHelper.FormatearGMS(azRad * (180.0 / Math.PI));
                    radioStr = "RECTA";
                }
                else if (entity is AlignmentArc arc)
                {
                    elemTag = $"C-{arcIdx++}";
                    startPt = arc.StartPoint; endPt = arc.EndPoint; startSt = arc.StartStation; endSt = arc.EndStation; length = arc.Length;
                    paramAngulo = "Δ: " + GeometriaHelper.FormatearGMS(arc.Delta * (180.0 / Math.PI));
                    radioStr = arc.Radius.ToString("F2");

                    DatosCurvaHorizontal? dato = curvasHorizontales.Find(c => c.Elemento == elemTag);
                    if (dato != null) cumpleStr = dato.CumpleRadioMinimo ? "SI" : "NO ⚠";
                }

                tbl.Cells[row, 0].TextString = elemTag;
                tbl.Cells[row, 1].TextString = GeometriaHelper.FormatearAbscisa(startSt);
                tbl.Cells[row, 2].TextString = GeometriaHelper.FormatearAbscisa(endSt);
                tbl.Cells[row, 3].TextString = startPt.Y.ToString("F2");
                tbl.Cells[row, 4].TextString = startPt.X.ToString("F2");
                tbl.Cells[row, 5].TextString = endPt.Y.ToString("F2");
                tbl.Cells[row, 6].TextString = endPt.X.ToString("F2");
                tbl.Cells[row, 7].TextString = paramAngulo;
                tbl.Cells[row, 8].TextString = radioStr;
                tbl.Cells[row, 9].TextString = length.ToString("F2");
                tbl.Cells[row, 10].TextString = cumpleStr;
                for (int col = 0; col < headers.Length; col++)
                {
                    tbl.Cells[row, col].TextHeight = 1.8;
                    tbl.Cells[row, col].Alignment = CellAlignment.MiddleCenter;
                }
            }

            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            btr.AppendEntity(tbl);
            tr.AddNewlyCreatedDBObject(tbl, true);
        }
    }
}
