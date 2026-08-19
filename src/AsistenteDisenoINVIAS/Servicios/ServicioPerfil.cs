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
    /// Pestaña 2: genera el perfil del terreno natural (MDT), calcula
    /// automáticamente una rasante que respeta la pendiente máxima y la
    /// entretangencia mínima de INVIAS, e inserta la vista de perfil nativa de
    /// Civil 3D. Se añade, respecto a la versión original, el cálculo de la
    /// distancia de visibilidad de parada como verificación adicional de cada
    /// curva vertical y un cuadro técnico de PVIs (que antes no existía).
    /// </summary>
    public static class ServicioPerfil
    {
        private struct PviDefinition
        {
            public double Station;
            public double Elevation;
            public double CurveLength;
        }

        /// <summary>Devuelve el ObjectId de la rasante nativa creada (Profile.CreateByLayout), o ObjectId.Null si no se pudo procesar.</summary>
        public static ObjectId ProcesarPerfil(
            Transaction tr,
            CivilDocument civilDoc,
            Database db,
            ObjectId alignId,
            ObjectId surfaceId,
            Point3d insertPt,
            ParametrosEntrada parametros,
            ResultadoDiseno resultado)
        {
            Alignment? alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment;
            Autodesk.Civil.DatabaseServices.Surface? surface = tr.GetObject(surfaceId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
            if (alignment == null || surface == null) return ObjectId.Null;

            double vtr = parametros.VelocidadDiseno;
            double pMax = parametros.PendienteMaximaAdmisible > 0 ? parametros.PendienteMaximaAdmisible : InviasNormativa.PendienteMaxima(parametros.CategoriaViaIdx, vtr);
            double lMinTangente = parametros.LongitudMinimaTangenteVertical > 0 ? parametros.LongitudMinimaTangenteVertical : InviasNormativa.LongitudMinimaTangente(vtr);
            double kCrest = parametros.KMinimoCresta > 0 ? parametros.KMinimoCresta : InviasNormativa.KMinimoCresta(vtr);
            double kSag = parametros.KMinimoColumpio > 0 ? parametros.KMinimoColumpio : InviasNormativa.KMinimoColumpio(vtr);
            double aLimite = InviasNormativa.DiferenciaAlgebraicaMinima;
            double lMinCurva = InviasNormativa.LongitudMinimaVisual(vtr);
            double dp = InviasNormativa.DistanciaVisibilidadParada(vtr);

            ObjectId profileStyleId = civilDoc.Styles.ProfileStyles.Count > 0 ? civilDoc.Styles.ProfileStyles[0] : ObjectId.Null;
            ObjectId profileLabelSetId = civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles.Count > 0 ? civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles[0] : ObjectId.Null;
            ObjectId profileViewStyleId = civilDoc.Styles.ProfileViewStyles.Count > 0 ? civilDoc.Styles.ProfileViewStyles[0] : ObjectId.Null;
            ObjectId bandSetStyleId = civilDoc.Styles.ProfileViewBandSetStyles.Count > 0 ? civilDoc.Styles.ProfileViewBandSetStyles[0] : ObjectId.Null;

            string profileTNName = "TN_" + alignment.Name;
            ObjectId profileTNId = Profile.CreateFromSurface(profileTNName, alignId, surfaceId, db.Clayer, profileStyleId, profileLabelSetId);
            Profile? profileTN = tr.GetObject(profileTNId, OpenMode.ForWrite) as Profile;
            if (profileTN != null) profileTN.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 3);

            ObjectId layoutStyleId = civilDoc.Styles.ProfileStyles.Count > 1 ? civilDoc.Styles.ProfileStyles[1] : profileStyleId;
            string rasanteName = "Rasante_INVIAS_" + alignment.Name;
            ObjectId rasanteId = Profile.CreateByLayout(rasanteName, alignId, db.Clayer, layoutStyleId, profileLabelSetId);
            Profile? rasante = tr.GetObject(rasanteId, OpenMode.ForWrite) as Profile;
            if (rasante != null) rasante.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 1);

            resultado.CurvasVerticales.Clear();

            if (profileTN != null && rasante != null)
            {
                List<PviDefinition> pviList = new List<PviDefinition>();
                pviList.Add(new PviDefinition { Station = alignment.StartingStation, Elevation = profileTN.ElevationAt(alignment.StartingStation), CurveLength = 0.0 });

                List<double> sampledStations = new List<double>();
                double currentSt = alignment.StartingStation + lMinTangente;
                while (currentSt <= alignment.EndingStation - lMinTangente) { sampledStations.Add(currentSt); currentSt += lMinTangente; }

                for (int i = 0; i < sampledStations.Count; i++)
                {
                    double stCurr = sampledStations[i]; double zCurr = profileTN.ElevationAt(stCurr);
                    double stPrev = (i == 0) ? alignment.StartingStation : sampledStations[i - 1]; double zPrev = profileTN.ElevationAt(stPrev);
                    double stNext = (i == sampledStations.Count - 1) ? alignment.EndingStation : sampledStations[i + 1]; double zNext = profileTN.ElevationAt(stNext);

                    double g1 = ((zCurr - zPrev) / (stCurr - stPrev)) * 100.0;
                    double g2 = ((zNext - zCurr) / (stNext - stCurr)) * 100.0;

                    if (g1 > pMax) zCurr = zPrev + (pMax / 100.0) * (stCurr - stPrev);
                    else if (g1 < -pMax) zCurr = zPrev - (pMax / 100.0) * (stCurr - stPrev);

                    double A = Math.Abs(g2 - g1);
                    double calculatedLv = 0.0;
                    string tipo = "N/A";
                    double kAplicado = 0.0;
                    double lvVisibilidad = 0.0;
                    bool cumple = true;
                    string observaciones = "";

                    if (A >= aLimite)
                    {
                        bool esCresta = g1 > g2;
                        tipo = esCresta ? "Cresta" : "Columpio";
                        kAplicado = esCresta ? kCrest : kSag;
                        double lvComodidad = kAplicado * A;
                        lvVisibilidad = InviasNormativa.LongitudMinimaPorVisibilidad(A, dp, esCresta);

                        double lvRequerida = Math.Max(lvComodidad, Math.Max(lvVisibilidad, lMinCurva));
                        double espacioDisponible = Math.Min(stCurr - stPrev, stNext - stCurr) * 0.70;
                        calculatedLv = Math.Min(lvRequerida, espacioDisponible);
                        if (calculatedLv < 10.0) calculatedLv = 10.0;

                        cumple = calculatedLv >= lvRequerida - 0.5;
                        if (!cumple)
                        {
                            observaciones = $"Lv adoptada ({calculatedLv:F1} m) limitada por la entretangencia disponible; la longitud requerida por comodidad/visibilidad era {lvRequerida:F1} m.";
                            resultado.RegistrarAdvertencia($"PVI {GeometriaHelper.FormatearAbscisa(stCurr)} (Perfil): {observaciones}");
                        }
                    }

                    resultado.CurvasVerticales.Add(new DatosCurvaVertical
                    {
                        Elemento = $"PVI-{resultado.CurvasVerticales.Count + 1}",
                        Abscisa = stCurr,
                        Cota = zCurr,
                        PendienteEntrada = g1,
                        PendienteSalida = g2,
                        DiferenciaAlgebraica = A,
                        Tipo = tipo,
                        KAplicado = kAplicado,
                        LongitudCurva = calculatedLv,
                        LongitudMinimaComodidad = A >= aLimite ? kAplicado * A : 0.0,
                        LongitudMinimaVisibilidad = lvVisibilidad,
                        CumpleLongitudMinima = cumple,
                        Observaciones = observaciones
                    });

                    pviList.Add(new PviDefinition { Station = stCurr, Elevation = zCurr, CurveLength = calculatedLv });
                }
                pviList.Add(new PviDefinition { Station = alignment.EndingStation, Elevation = profileTN.ElevationAt(alignment.EndingStation), CurveLength = 0.0 });

                foreach (PviDefinition pviDef in pviList)
                {
                    if (pviDef.CurveLength >= 10.0)
                    {
                        try { rasante.PVIs.AddPVISymParabola(pviDef.Station, pviDef.Elevation, pviDef.CurveLength); }
                        catch { rasante.PVIs.AddPVI(pviDef.Station, pviDef.Elevation); }
                    }
                    else rasante.PVIs.AddPVI(pviDef.Station, pviDef.Elevation);
                }
            }

            string profileViewName = "Perfil_" + alignment.Name;
            ProfileView.Create(alignId, insertPt, profileViewName, bandSetStyleId, profileViewStyleId);

            if (resultado.CurvasVerticales.Count > 0)
            {
                Point3d tablaPt = new Point3d(insertPt.X, insertPt.Y - 60.0, insertPt.Z);
                CrearTablaPerfilRasante(db, tr, tablaPt, resultado.CurvasVerticales);
            }

            resultado.RegistrarElementoNativo($"Superficie de terreno natural '{profileTNName}' y rasante nativa '{rasanteName}' con vista de perfil '{profileViewName}'.");
            resultado.PerfilProcesado = true;

            return rasanteId;
        }

        private static void CrearTablaPerfilRasante(Database db, Transaction tr, Point3d insertPt, List<DatosCurvaVertical> curvas)
        {
            Table tbl = new Table();
            tbl.SetDatabaseDefaults();
            tbl.Position = insertPt;
            tbl.SetSize(curvas.Count + 2, 10);
            tbl.Cells[0, 0].TextString = "CUADRO DE PVIs Y CURVAS VERTICALES DE RASANTE (INVIAS)";
            tbl.Cells[0, 0].TextHeight = 3.0;
            tbl.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            string[] headers = { "PVI", "Abscisa", "Cota (m)", "g1 (%)", "g2 (%)", "A (%)", "Tipo", "K aplicado", "Lv (m)", "Cumple" };
            for (int col = 0; col < headers.Length; col++)
            {
                tbl.Cells[1, col].TextString = headers[col];
                tbl.Cells[1, col].TextHeight = 2.0;
                tbl.Cells[1, col].Alignment = CellAlignment.MiddleCenter;
                tbl.Columns[col].Width = 24.0;
            }

            for (int i = 0; i < curvas.Count; i++)
            {
                var c = curvas[i];
                int row = i + 2;
                tbl.Cells[row, 0].TextString = c.Elemento;
                tbl.Cells[row, 1].TextString = GeometriaHelper.FormatearAbscisa(c.Abscisa);
                tbl.Cells[row, 2].TextString = c.Cota.ToString("F2");
                tbl.Cells[row, 3].TextString = c.PendienteEntrada.ToString("F2");
                tbl.Cells[row, 4].TextString = c.PendienteSalida.ToString("F2");
                tbl.Cells[row, 5].TextString = c.DiferenciaAlgebraica.ToString("F2");
                tbl.Cells[row, 6].TextString = c.Tipo;
                tbl.Cells[row, 7].TextString = c.Tipo == "N/A" ? "-" : c.KAplicado.ToString("F1");
                tbl.Cells[row, 8].TextString = c.Tipo == "N/A" ? "-" : c.LongitudCurva.ToString("F1");
                tbl.Cells[row, 9].TextString = c.Tipo == "N/A" ? "-" : (c.CumpleLongitudMinima ? "SI" : "NO ⚠");
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
