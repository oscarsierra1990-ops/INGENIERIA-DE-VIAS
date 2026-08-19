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
                // PROPUESTA de rasante (no solo verificación): se recorren los
                // quiebres candidatos del terreno y se acepta cada uno como PVI
                // real SOLO si existe espacio para una curva vertical de la
                // longitud exigida por INVIAS (comodidad K·A, visibilidad de
                // parada y entretangencia mínima). Cuando el espacio no alcanza,
                // el vértice se omite (el trazado se suaviza recto en ese tramo)
                // en vez de construir una curva más corta que incumpla la
                // normativa. Así toda curva vertical generada cumple siempre el
                // mínimo normativo, y cada omisión queda documentada como una
                // decisión de diseño con su justificación.
                double zInicio = profileTN.ElevationAt(alignment.StartingStation);
                List<PviDefinition> pviList = new List<PviDefinition>
                {
                    new PviDefinition { Station = alignment.StartingStation, Elevation = zInicio, CurveLength = 0.0 }
                };

                List<double> candidatos = new List<double>();
                double currentSt = alignment.StartingStation + lMinTangente;
                while (currentSt <= alignment.EndingStation - lMinTangente) { candidatos.Add(currentSt); currentSt += lMinTangente; }

                double ultimaEstacion = alignment.StartingStation;
                double ultimaElevacion = zInicio;
                double ultimaLongitudCurva = 0.0;

                for (int i = 0; i < candidatos.Count; i++)
                {
                    double stCand = candidatos[i];
                    double zCand = profileTN.ElevationAt(stCand);

                    double stRef = (i == candidatos.Count - 1) ? alignment.EndingStation : candidatos[i + 1];
                    double zRef = profileTN.ElevationAt(stRef);

                    double g1 = ((zCand - ultimaElevacion) / (stCand - ultimaEstacion)) * 100.0;
                    if (g1 > pMax) { zCand = ultimaElevacion + (pMax / 100.0) * (stCand - ultimaEstacion); g1 = pMax; }
                    else if (g1 < -pMax) { zCand = ultimaElevacion - (pMax / 100.0) * (stCand - ultimaEstacion); g1 = -pMax; }

                    double g2 = ((zRef - zCand) / (stRef - stCand)) * 100.0;
                    double A = Math.Abs(g2 - g1);

                    if (A < aLimite)
                    {
                        // Quiebre insignificante: no requiere curva vertical, se
                        // acopla directamente a la rasante como punto de paso.
                        resultado.CurvasVerticales.Add(new DatosCurvaVertical
                        {
                            Elemento = $"PVI-{resultado.CurvasVerticales.Count + 1}",
                            Abscisa = stCand,
                            Cota = zCand,
                            PendienteEntrada = g1,
                            PendienteSalida = g2,
                            DiferenciaAlgebraica = A,
                            Tipo = "N/A",
                            CumpleLongitudMinima = true
                        });
                        pviList.Add(new PviDefinition { Station = stCand, Elevation = zCand, CurveLength = 0.0 });
                        ultimaEstacion = stCand; ultimaElevacion = zCand; ultimaLongitudCurva = 0.0;
                        continue;
                    }

                    bool esCresta = g1 > g2;
                    string tipo = esCresta ? "Cresta" : "Columpio";
                    double kAplicado = esCresta ? kCrest : kSag;
                    double lvComodidad = kAplicado * A;
                    double lvVisibilidad = InviasNormativa.LongitudMinimaPorVisibilidad(A, dp, esCresta);
                    double lvRequerida = Math.Max(lvComodidad, Math.Max(lvVisibilidad, lMinCurva));

                    double finCurvaAnterior = ultimaEstacion + ultimaLongitudCurva / 2.0;
                    double inicioCurvaRequerido = stCand - lvRequerida / 2.0;

                    if (inicioCurvaRequerido - finCurvaAnterior < lMinTangente)
                    {
                        // No cabe una curva conforme en este punto sin invadir la
                        // entretangencia mínima con la curva anterior: se omite
                        // el vértice y el tramo queda recto entre las curvas
                        // vecinas, en vez de construir una curva corta que
                        // incumpliría la Tabla 4.3/4.4/4.5 de INVIAS.
                        resultado.RegistrarDecision(
                            $"Perfil: se omitió un vértice de rasante cerca de {GeometriaHelper.FormatearAbscisa(stCand)} porque la entretangencia disponible " +
                            $"({Math.Max(0, inicioCurvaRequerido - finCurvaAnterior):F1} m) es menor que la mínima normativa ({lMinTangente:F1} m); " +
                            "el tramo se diseñó recto entre las curvas vecinas para no incumplir la normativa en vez de construir una curva vertical corta.");
                        continue; // no avanza: se reintenta el siguiente candidato desde el mismo último PVI aceptado
                    }

                    var dato = new DatosCurvaVertical
                    {
                        Elemento = $"PVI-{resultado.CurvasVerticales.Count + 1}",
                        Abscisa = stCand,
                        Cota = zCand,
                        PendienteEntrada = g1,
                        PendienteSalida = g2,
                        DiferenciaAlgebraica = A,
                        Tipo = tipo,
                        KAplicado = kAplicado,
                        LongitudCurva = lvRequerida,
                        LongitudMinimaComodidad = lvComodidad,
                        LongitudMinimaVisibilidad = lvVisibilidad,
                        CumpleLongitudMinima = true,
                        Observaciones = $"Lv = max(comodidad K·A = {lvComodidad:F1} m, visibilidad de parada = {lvVisibilidad:F1} m, mínima visual = {lMinCurva:F1} m)."
                    };
                    resultado.CurvasVerticales.Add(dato);
                    resultado.RegistrarDecision(
                        $"{dato.Elemento} (Perfil): curva vertical de {tipo.ToLower()} de Lv = {lvRequerida:F1} m propuesta en {GeometriaHelper.FormatearAbscisa(stCand)}, " +
                        $"dimensionada por el criterio más exigente entre comodidad (K = {kAplicado:F1}) y visibilidad de parada (Dp = {dp:F1} m para Vtr = {vtr:F0} km/h).");

                    pviList.Add(new PviDefinition { Station = stCand, Elevation = zCand, CurveLength = lvRequerida });
                    ultimaEstacion = stCand; ultimaElevacion = zCand; ultimaLongitudCurva = lvRequerida;
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
