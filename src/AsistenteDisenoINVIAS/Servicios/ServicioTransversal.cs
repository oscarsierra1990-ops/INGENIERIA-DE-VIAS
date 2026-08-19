using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AsistenteDisenoINVIAS.Modelo;
using AsistenteDisenoINVIAS.Normativa;
using AsistenteDisenoINVIAS.Utilidades;
using Table = Autodesk.AutoCAD.DatabaseServices.Table;

namespace AsistenteDisenoINVIAS.Servicios
{
    /// <summary>
    /// Pestaña 3: sobreanchos, peraltes y bordes de vía como alineamientos de
    /// desfase (offset) nativos de Civil 3D.
    ///
    /// CORRECCIÓN respecto a la versión original: la inyección de peraltes
    /// correlacionaba <c>alignment.SuperelevationCurves</c> con la lista de
    /// curvas del eje por POSICIÓN (índice 0,1,2…). Esa suposición se rompe en
    /// cuanto Civil 3D omite una curva demasiado amplia para requerir peralte,
    /// o agrupa/renumera las curvas de peralte de otra forma: el peralte de una
    /// curva terminaba aplicado a otra. Ahora cada <see cref="SuperelevationCurve"/>
    /// se identifica por la estación real de sus estaciones críticas existentes
    /// (dato ya presente antes de limpiarlas) y se empareja con la curva
    /// geométrica del eje cuyo rango de abscisas la contiene.
    /// </summary>
    public static class ServicioTransversal
    {
        public static void ProcesarTransversal(
            Transaction tr,
            CivilDocument civilDoc,
            Database db,
            Editor ed,
            ObjectId alignId,
            Point3d insertPt,
            ParametrosEntrada parametros,
            ResultadoDiseno resultado)
        {
            Alignment? alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment;
            if (alignment == null) return;

            double anchoCarril = parametros.AnchoCarril;
            double vtr = parametros.VelocidadDiseno;
            double lVehiculo = parametros.LongitudVehiculo;
            double iMax = parametros.FriccionTransversalMaxima > 0 ? parametros.FriccionTransversalMaxima : InviasNormativa.FriccionTransversalMaxima(vtr);

            // 1. CÁLCULO MATEMÁTICO INVIAS a partir del radio REAL de cada arco
            //    (ya no viene alterado por la Pestaña 1, ver ServicioPlanta).
            List<DatosCurvaHorizontal> curvasLocal = new List<DatosCurvaHorizontal>();
            int arcIdx = 1;
            foreach (AlignmentEntity entity in alignment.Entities)
            {
                if (entity is AlignmentArc arc)
                {
                    double r = arc.Radius;
                    double s = InviasNormativa.Sobreancho(r, lVehiculo, vtr);
                    double eMax = InviasNormativa.PeralteMaximo(r);
                    double lt = InviasNormativa.LongitudTransicion(anchoCarril, eMax, iMax);

                    curvasLocal.Add(new DatosCurvaHorizontal
                    {
                        Elemento = $"C-{arcIdx++}",
                        AbscisaInicio = arc.StartStation,
                        AbscisaFin = arc.EndStation,
                        Radio = r,
                        GiraDerecha = GeometriaHelper.EsGiroDerecha(arc),
                        SobreanchoMaximo = s,
                        PeralteMaximo = eMax,
                        LongitudTransicion = lt
                    });
                }
            }

            // 2. CREACIÓN DE DESFASES (bordes de vía) Y TRANSICIÓN DINÁMICA DE
            //    SOBREANCHO (regla INVIAS 2/3 en tangente recta y 1/3 en curva).
            ObjectId styleId = civilDoc.Styles.AlignmentStyles.Count > 0 ? civilDoc.Styles.AlignmentStyles[0] : ObjectId.Null;
            try
            {
                ObjectId leftOffsetId = Alignment.CreateOffsetAlignment(alignment.Name + "_Borde_Izquierdo", alignId, -anchoCarril, styleId, alignment.StartingStation, alignment.EndingStation);
                ObjectId rightOffsetId = Alignment.CreateOffsetAlignment(alignment.Name + "_Borde_Derecho", alignId, anchoCarril, styleId, alignment.StartingStation, alignment.EndingStation);

                Alignment? leftAlign = tr.GetObject(leftOffsetId, OpenMode.ForWrite) as Alignment;
                Alignment? rightAlign = tr.GetObject(rightOffsetId, OpenMode.ForWrite) as Alignment;

                if (leftAlign != null && rightAlign != null)
                {
                    foreach (var c in curvasLocal)
                    {
                        if (c.SobreanchoMaximo > 0.05)
                        {
                            double stFullStart = c.AbscisaInicio + (1.0 / 3.0) * c.LongitudTransicion;
                            double stFullEnd = c.AbscisaFin - (1.0 / 3.0) * c.LongitudTransicion;
                            if (stFullStart >= stFullEnd)
                            {
                                double mid = (c.AbscisaInicio + c.AbscisaFin) / 2.0;
                                stFullStart = mid - 0.5; stFullEnd = mid + 0.5;
                            }

                            Alignment targetAlign = c.GiraDerecha ? rightAlign : leftAlign;
                            double targetWidth = c.GiraDerecha ? (anchoCarril + c.SobreanchoMaximo) : -(anchoCarril + c.SobreanchoMaximo);

                            targetAlign.OffsetAlignmentInfo.AddWidening(stFullStart, stFullEnd, targetWidth);

                            // Elimina el "escalón" de longitud cero que Civil 3D
                            // genera por defecto en la transición, reemplazándolo
                            // por la rampa Lt calculada según INVIAS.
                            dynamic offsetInfo = targetAlign.OffsetAlignmentInfo;
                            foreach (dynamic trans in offsetInfo.Transitions)
                            {
                                try
                                {
                                    if (trans.TransitionDescription.Length < 0.1)
                                        trans.TransitionDescription.Length = c.LongitudTransicion;
                                }
                                catch { }
                            }
                        }
                    }
                }

                resultado.RegistrarElementoNativo($"Alineamientos de borde de vía nativos '{alignment.Name}_Borde_Izquierdo' y '{alignment.Name}_Borde_Derecho' con sobreancho variable aplicado.");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[INVIAS] Detalle en desfases: {ex.Message}");
                resultado.RegistrarAdvertencia($"No se pudieron generar (o completar) los alineamientos de borde de vía: {ex.Message}");
            }

            // 3. INYECCIÓN NATIVA DE PERALTES EN EL ALINEAMIENTO C3D
            CalcularPeraltesNativosCivil3D(alignment, curvasLocal, resultado);

            // 4. CREACIÓN DE VISTA DE PERALTES NATIVA MEDIANTE REFLEXIÓN
            //    (evita errores de sobrecarga entre distintas versiones de la API).
            try
            {
                Type viewType = typeof(SuperelevationView);
                System.Reflection.MethodInfo[] methods = viewType.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

                foreach (var mi in methods)
                {
                    if (mi.Name != "Create") continue;
                    var p = mi.GetParameters();
                    if (p.Length == 3 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(ObjectId) && p[2].ParameterType == typeof(Point3d))
                    {
                        Point3d superViewPt = new Point3d(insertPt.X, insertPt.Y - 50.0, insertPt.Z);
                        mi.Invoke(null, new object[] { "Vista_Peraltes_" + alignment.Name, alignId, superViewPt });
                        break;
                    }
                    else if (p.Length == 4 && p[0].ParameterType == typeof(ObjectId) && p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(ObjectId) && p[3].ParameterType == typeof(Point3d))
                    {
                        ObjectId sViewStyleId = civilDoc.Styles.SuperelevationViewStyles.Count > 0 ? civilDoc.Styles.SuperelevationViewStyles[0] : ObjectId.Null;
                        Point3d superViewPt = new Point3d(insertPt.X, insertPt.Y - 50.0, insertPt.Z);
                        mi.Invoke(null, new object[] { alignId, "Vista_Peraltes_" + alignment.Name, sViewStyleId, superViewPt });
                        break;
                    }
                }
            }
            catch { }

            // 5. TABLA TÉCNICA DE TRANSICIONES INVIAS
            CrearTablaTransversal(db, tr, insertPt, curvasLocal);

            // 6. Fusiona los resultados con la lista maestra que alimenta la
            //    memoria descriptiva (conserva lo calculado en Planta si existe).
            foreach (var local in curvasLocal)
            {
                DatosCurvaHorizontal? existente = resultado.CurvasHorizontales.Find(c => c.Elemento == local.Elemento);
                if (existente != null)
                {
                    existente.PeralteMaximo = local.PeralteMaximo;
                    existente.SobreanchoMaximo = local.SobreanchoMaximo;
                    existente.LongitudTransicion = local.LongitudTransicion;
                }
                else
                {
                    resultado.CurvasHorizontales.Add(local);
                }
            }

            if (curvasLocal.Count > 0)
            {
                resultado.RegistrarDecision(
                    $"Transversal: se propusieron y aplicaron nativamente sobreancho y peralte en {curvasLocal.Count} curva(s) circular(es) " +
                    $"(ancho de carril {anchoCarril:F2} m, vehículo {parametros.VehiculoDiseno}), con transición 2/3 en tangente y 1/3 en curva; " +
                    "ver el detalle por curva en la Sección 4.");
            }

            resultado.TransversalProcesado = true;
        }

        private static void CalcularPeraltesNativosCivil3D(Alignment alignment, List<DatosCurvaHorizontal> curves, ResultadoDiseno resultado)
        {
            SuperelevationCrossSegmentType leftLane = (SuperelevationCrossSegmentType)0;
            SuperelevationCrossSegmentType rightLane = (SuperelevationCrossSegmentType)1;

            foreach (string name in Enum.GetNames(typeof(SuperelevationCrossSegmentType)))
            {
                string lower = name.ToLower();
                if (lower.Contains("left") && (lower.Contains("out") || lower.Contains("ext")) && !lower.Contains("shoulder") && !lower.Contains("inside"))
                    leftLane = (SuperelevationCrossSegmentType)Enum.Parse(typeof(SuperelevationCrossSegmentType), name);

                if (lower.Contains("right") && (lower.Contains("out") || lower.Contains("ext")) && !lower.Contains("shoulder") && !lower.Contains("inside"))
                    rightLane = (SuperelevationCrossSegmentType)Enum.Parse(typeof(SuperelevationCrossSegmentType), name);
            }

            try
            {
                if (alignment.SuperelevationCurves.Count != curves.Count)
                {
                    resultado.RegistrarAdvertencia($"Civil 3D generó {alignment.SuperelevationCurves.Count} curva(s) de peralte automáticas frente a {curves.Count} curva(s) circulares del eje. Se empareja cada curva por estación real; verifique manualmente la vista de peraltes.");
                }

                foreach (SuperelevationCurve supCurve in alignment.SuperelevationCurves)
                {
                    // Estación de referencia tomada ANTES de limpiar las estaciones
                    // críticas que Civil 3D generó automáticamente, para ubicar a
                    // qué curva circular del eje pertenece esta curva de peralte.
                    double estacionReferencia = double.NaN;
                    if (supCurve.CriticalStations.Count > 0)
                    {
                        double suma = 0;
                        foreach (SuperelevationCriticalStation cs0 in supCurve.CriticalStations) suma += cs0.Station;
                        estacionReferencia = suma / supCurve.CriticalStations.Count;
                    }

                    DatosCurvaHorizontal? c = double.IsNaN(estacionReferencia)
                        ? null
                        : EncontrarCurvaPorEstacion(curves, estacionReferencia);

                    if (c == null) continue; // no se pudo identificar con certeza: no se toca esta curva.

                    for (int i = supCurve.CriticalStations.Count - 1; i >= 0; i--)
                    {
                        try { supCurve.CriticalStations.RemoveAt(i); } catch { }
                    }

                    double st0 = c.AbscisaInicio - (2.0 / 3.0) * c.LongitudTransicion;
                    double st1 = c.AbscisaInicio + (1.0 / 3.0) * c.LongitudTransicion;
                    double st2 = c.AbscisaFin - (1.0 / 3.0) * c.LongitudTransicion;
                    double st3 = c.AbscisaFin + (2.0 / 3.0) * c.LongitudTransicion;

                    double eDec = c.PeralteMaximo / 100.0;
                    double outS = eDec;
                    double inS = -eDec;

#pragma warning disable CS0618
                    supCurve.CriticalStations.Add(st0, SuperelevationCriticalStationType.EndNormalCrown);
                    supCurve.CriticalStations.Add(st1, SuperelevationCriticalStationType.BeginFullSuper);
                    supCurve.CriticalStations.Add(st2, SuperelevationCriticalStationType.EndFullSuper);
                    supCurve.CriticalStations.Add(st3, SuperelevationCriticalStationType.BeginNormalCrown);

                    foreach (SuperelevationCriticalStation cs in supCurve.CriticalStations)
                    {
                        if (Math.Abs(cs.Station - st0) < 0.01 || Math.Abs(cs.Station - st3) < 0.01)
                        {
                            cs.SetSlope(leftLane, -0.02);
                            cs.SetSlope(rightLane, -0.02);
                        }
                        else if (Math.Abs(cs.Station - st1) < 0.01 || Math.Abs(cs.Station - st2) < 0.01)
                        {
                            cs.SetSlope(leftLane, c.GiraDerecha ? outS : inS);
                            cs.SetSlope(rightLane, c.GiraDerecha ? inS : outS);
                        }
                    }
#pragma warning restore CS0618
                }

                resultado.RegistrarElementoNativo($"Peraltes inyectados nativamente en {alignment.SuperelevationCurves.Count} curva(s) de peralte del alineamiento '{alignment.Name}'.");
            }
            catch (Exception ex)
            {
                resultado.RegistrarAdvertencia($"No se pudieron inyectar los peraltes nativos: {ex.Message}");
            }
        }

        private static DatosCurvaHorizontal? EncontrarCurvaPorEstacion(List<DatosCurvaHorizontal> curvas, double estacion)
        {
            foreach (var c in curvas)
            {
                if (estacion >= c.AbscisaInicio - 1.0 && estacion <= c.AbscisaFin + 1.0) return c;
            }
            DatosCurvaHorizontal? mejor = null;
            double mejorDist = double.MaxValue;
            foreach (var c in curvas)
            {
                double centro = (c.AbscisaInicio + c.AbscisaFin) / 2.0;
                double dist = Math.Abs(estacion - centro);
                if (dist < mejorDist) { mejorDist = dist; mejor = c; }
            }
            return mejor;
        }

        private static void CrearTablaTransversal(Database db, Transaction tr, Point3d insertPt, List<DatosCurvaHorizontal> curves)
        {
            if (curves.Count == 0) return;

            BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            Table tbl = new Table();
            tbl.SetDatabaseDefaults();
            tbl.Position = insertPt;
            tbl.SetSize(curves.Count + 2, 10);
            tbl.Cells[0, 0].TextString = "CUADRO DE TRANSICIONES DE SOBREANCHO Y PERALTE (INVIAS)";
            tbl.Cells[0, 0].TextHeight = 3.0;
            tbl.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            string[] h = { "Curva", "Inicio Trans. (2/3 Lt)", "PC", "Sobreancho Máx. (PC+1/3Lt)", "PT", "Fin Trans. (2/3 Lt)", "Radio (m)", "S (m)", "e máx (%)", "Lt (m)" };
            for (int col = 0; col < h.Length; col++)
            {
                tbl.Cells[1, col].TextString = h[col];
                tbl.Cells[1, col].TextHeight = 2.0;
                tbl.Cells[1, col].Alignment = CellAlignment.MiddleCenter;
                tbl.Columns[col].Width = 30.0;
            }

            int r = 2;
            foreach (var c in curves)
            {
                double stEntryStart = c.AbscisaInicio - (2.0 / 3.0) * c.LongitudTransicion;
                double stFullStart = c.AbscisaInicio + (1.0 / 3.0) * c.LongitudTransicion;
                double stExitEnd = c.AbscisaFin + (2.0 / 3.0) * c.LongitudTransicion;

                tbl.Cells[r, 0].TextString = c.Elemento;
                tbl.Cells[r, 1].TextString = GeometriaHelper.FormatearAbscisa(stEntryStart);
                tbl.Cells[r, 2].TextString = GeometriaHelper.FormatearAbscisa(c.AbscisaInicio);
                tbl.Cells[r, 3].TextString = GeometriaHelper.FormatearAbscisa(stFullStart);
                tbl.Cells[r, 4].TextString = GeometriaHelper.FormatearAbscisa(c.AbscisaFin);
                tbl.Cells[r, 5].TextString = GeometriaHelper.FormatearAbscisa(stExitEnd);
                tbl.Cells[r, 6].TextString = c.Radio.ToString("F2");
                tbl.Cells[r, 7].TextString = c.SobreanchoMaximo.ToString("F2");
                tbl.Cells[r, 8].TextString = c.PeralteMaximo.ToString("F2");
                tbl.Cells[r, 9].TextString = c.LongitudTransicion.ToString("F2");
                for (int col = 0; col < h.Length; col++)
                {
                    tbl.Cells[r, col].TextHeight = 1.8;
                    tbl.Cells[r, col].Alignment = CellAlignment.MiddleCenter;
                }
                r++;
            }
            btr.AppendEntity(tbl);
            tr.AddNewlyCreatedDBObject(tbl, true);
        }
    }
}
