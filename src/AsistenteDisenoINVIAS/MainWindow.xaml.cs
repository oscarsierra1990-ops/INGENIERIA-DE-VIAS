using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AsistenteDisenoINVIAS.Modelo;
using AsistenteDisenoINVIAS.Normativa;
using AsistenteDisenoINVIAS.Reportes;
using AsistenteDisenoINVIAS.Servicios;
using System;
using System.Windows;
using System.Windows.Controls;

namespace AsistenteDisenoINVIAS
{
    /// <summary>
    /// Orquesta las cuatro pestañas del asistente. Mantiene un único
    /// <see cref="ResultadoDiseno"/> por sesión de panel para que la Pestaña 4
    /// (Memoria) pueda documentar exactamente lo que las Pestañas 1-3
    /// calcularon y dibujaron en el modelo de Civil 3D.
    /// </summary>
    public partial class MainWindow : UserControl
    {
        public static ObjectId SelectedPolylineId = ObjectId.Null;

        private readonly ResultadoDiseno _resultado = new ResultadoDiseno();
        private ObjectId _alignmentId = ObjectId.Null;
        private ObjectId _rasanteId = ObjectId.Null;

        public MainWindow()
        {
            InitializeComponent();
        }

        // ==========================================
        // Normativa dinámica: Vtr sugerida por categoría + terreno
        // ==========================================
        private void CmbNormativa_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCategoriaVia == null || CmbTipoTerreno == null || CmbVtr == null) return;
            int catIdx = CmbCategoriaVia.SelectedIndex;
            int terIdx = CmbTipoTerreno.SelectedIndex;
            if (catIdx < 0 || terIdx < 0) return;

            double targetVtr = InviasNormativa.VelocidadPorDefecto(catIdx, terIdx);

            foreach (ComboBoxItem item in CmbVtr.Items)
            {
                if (item.Content.ToString() == targetVtr.ToString("F0")) { CmbVtr.SelectedItem = item; break; }
            }
        }

        // ==========================================
        // Superficies (MDT) para la Pestaña 2
        // ==========================================
        private void CmbSuperficies_Loaded(object sender, RoutedEventArgs e) { CargarSuperficiesEnComboBox(); }
        private void CmbSuperficies_DropDownOpened(object sender, EventArgs e) { CargarSuperficiesEnComboBox(); }

        private void CargarSuperficiesEnComboBox()
        {
            CmbSuperficies.Items.Clear();
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            CivilDocument civilDoc = CivilApplication.ActiveDocument;
            ObjectIdCollection surfaceIds = civilDoc.GetSurfaceIds();
            if (surfaceIds.Count == 0) return;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId surfId in surfaceIds)
                {
                    Autodesk.Civil.DatabaseServices.Surface? surf = tr.GetObject(surfId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                    if (surf != null) CmbSuperficies.Items.Add(new ComboBoxItem { Content = surf.Name, Tag = surfId });
                }
                tr.Commit();
            }
            if (CmbSuperficies.Items.Count > 0) CmbSuperficies.SelectedIndex = 0;
        }

        // ==========================================
        // PESTAÑA 1: PLANTA
        // ==========================================
        private void BtnSelectPolyline_Click(object sender, RoutedEventArgs e)
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            PromptEntityOptions peo = new PromptEntityOptions("\n[INVIAS] Seleccione la polilínea 2D del eje de la vía: ");
            peo.SetRejectMessage("\n¡Atención! Debe seleccionar una Polilínea 2D (LWPOLYLINE).");
            peo.AddAllowedClass(typeof(Autodesk.AutoCAD.DatabaseServices.Polyline), true);
            PromptEntityResult per = doc.Editor.GetEntity(peo);
            if (per.Status == PromptStatus.OK)
            {
                SelectedPolylineId = per.ObjectId;
                MessageBox.Show("✅ ¡Polilínea del eje seleccionada con éxito!", "Selección", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ActualizarParametrosDesdeUI()
        {
            var p = _resultado.Parametros;
            p.CategoriaViaIdx = CmbCategoriaVia.SelectedIndex;
            p.CategoriaVia = ((ComboBoxItem)CmbCategoriaVia.SelectedItem).Content.ToString() ?? "";
            p.TipoTerrenoIdx = CmbTipoTerreno.SelectedIndex;
            p.TipoTerreno = ((ComboBoxItem)CmbTipoTerreno.SelectedItem).Content.ToString() ?? "";
            p.VelocidadDiseno = Convert.ToDouble(((ComboBoxItem)CmbVtr.SelectedItem).Content);

            double anchoCarril = 3.65;
            double.TryParse(TxtAnchoCarril.Text, out anchoCarril);
            p.AnchoCarril = anchoCarril;

            if (CmbVehiculo.SelectedItem != null)
            {
                p.VehiculoDiseno = ((ComboBoxItem)CmbVehiculo.SelectedItem).Content.ToString() ?? "";
                p.LongitudVehiculo = CmbVehiculo.SelectedIndex == 0 ? 6.1 : CmbVehiculo.SelectedIndex == 1 ? 7.5 : 10.5;
            }
        }

        private void BtnProcesarPlanta_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPolylineId == ObjectId.Null)
            {
                MessageBox.Show("Primero seleccione la polilínea de eje.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database; Editor ed = doc.Editor; CivilDocument civilDoc = CivilApplication.ActiveDocument;

            ActualizarParametrosDesdeUI();

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                Alignment? alignment = ServicioPlanta.ProcesarPlanta(tr, civilDoc, db, SelectedPolylineId, _resultado.Parametros, _resultado);
                if (alignment == null)
                {
                    tr.Commit();
                    MessageBox.Show("No se pudo crear el alineamiento a partir de la polilínea seleccionada.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                _alignmentId = alignment.ObjectId;

                PromptPointOptions ppo = new PromptPointOptions("\n[INVIAS] Haga clic en el dibujo para ubicar el CUADRO DE ALINEAMIENTO: ");
                PromptPointResult ppr = ed.GetPoint(ppo);
                if (ppr.Status == PromptStatus.OK)
                    ServicioPlanta.CrearTablaAlineamientoCompleto(db, tr, alignment, ppr.Value, _resultado.CurvasHorizontales);

                tr.Commit();
            }

            ActualizarEstadoMemoria();
            int noCumplen = _resultado.CurvasHorizontales.FindAll(c => !c.CumpleRadioMinimo).Count;
            string aviso = noCumplen > 0 ? $"\n⚠️ {noCumplen} curva(s) no cumplen el radio mínimo normativo." : "";
            MessageBox.Show($"🚀 ¡Alineamiento Creado!\n\n• Vtr: {_resultado.Parametros.VelocidadDiseno:F0} km/h\n• Radio mínimo normativo: {_resultado.Parametros.RadioMinimoAdmisible:F1} m\n• Curvas detectadas: {_resultado.CurvasHorizontales.Count}{aviso}", "Planta Completada", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ==========================================
        // PESTAÑA 2: PERFIL & RASANTE
        // ==========================================
        private void BtnProcesarPerfil_Click(object sender, RoutedEventArgs e)
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database; Editor ed = doc.Editor; CivilDocument civilDoc = CivilApplication.ActiveDocument;

            if (CmbSuperficies.SelectedItem == null) { CargarSuperficiesEnComboBox(); return; }
            ObjectId surfaceId = (ObjectId)((ComboBoxItem)CmbSuperficies.SelectedItem).Tag;

            ObjectId alignId = _alignmentId;
            if (alignId == ObjectId.Null)
            {
                ObjectIdCollection alignIds = civilDoc.GetAlignmentIds();
                if (alignIds.Count == 0)
                {
                    MessageBox.Show("No se encontró ningún alineamiento. Procese primero la Pestaña 1.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                alignId = alignIds[alignIds.Count - 1];
                _alignmentId = alignId;
            }

            ActualizarParametrosDesdeUI();
            _resultado.Parametros.NombreSuperficie = ((ComboBoxItem)CmbSuperficies.SelectedItem).Content.ToString() ?? "";

            PromptPointOptions ppo = new PromptPointOptions("\n[INVIAS] Haga clic para dibujar el PERFIL C3D: ");
            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK) return;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                _rasanteId = ServicioPerfil.ProcesarPerfil(tr, civilDoc, db, alignId, surfaceId, ppr.Value, _resultado.Parametros, _resultado);
                tr.Commit();
            }

            ActualizarEstadoMemoria();
            MessageBox.Show($"🚀 ¡Rasante Creada!\nPendiente Máxima Aplicada: {_resultado.Parametros.PendienteMaximaAdmisible:F1}%\nCurvas verticales generadas: {_resultado.CurvasVerticales.FindAll(c => c.Tipo != "N/A").Count}", "INVIAS", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ==========================================
        // PESTAÑA 3: TRANSVERSALES (sobreancho, peralte, bordes de vía)
        // ==========================================
        private void BtnProcesarTransversal_Click(object sender, RoutedEventArgs e)
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database; Editor ed = doc.Editor; CivilDocument civilDoc = CivilApplication.ActiveDocument;

            ObjectId alignId = _alignmentId;
            if (alignId == ObjectId.Null)
            {
                ObjectIdCollection alignIds = civilDoc.GetAlignmentIds();
                if (alignIds.Count == 0)
                {
                    MessageBox.Show("No se encontró ningún Alineamiento. Procese primero la Pestaña 1.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                alignId = alignIds[alignIds.Count - 1];
                _alignmentId = alignId;
            }

            ActualizarParametrosDesdeUI();
            _resultado.Parametros.FriccionTransversalMaxima = InviasNormativa.FriccionTransversalMaxima(_resultado.Parametros.VelocidadDiseno);

            PromptPointOptions ppo = new PromptPointOptions("\n[INVIAS] Haga clic para insertar la TABLA TÉCNICA: ");
            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK) return;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ServicioTransversal.ProcesarTransversal(tr, civilDoc, db, ed, alignId, ppr.Value, _resultado.Parametros, _resultado);
                tr.Commit();
            }

            ActualizarEstadoMemoria();
            MessageBox.Show("🚀 ¡Transiciones Completadas!\n\n• Bordes de vía: alineamientos de desfase nativos con sobreancho variable.\n• Peraltes: inyectados nativamente y emparejados por estación real.", "INVIAS", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnGenerarCorredor_Click(object sender, RoutedEventArgs e)
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database; CivilDocument civilDoc = CivilApplication.ActiveDocument;

            if (_alignmentId == ObjectId.Null || _rasanteId == ObjectId.Null)
            {
                MessageBox.Show("Para generar el corredor nativo primero procese la Pestaña 1 (Planta) y la Pestaña 2 (Perfil/Rasante).", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool exito;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                exito = ServicioCorredor.CrearCorredorBasico(tr, civilDoc, db, _alignmentId, _rasanteId, _resultado.Parametros, _resultado);
                tr.Commit();
            }

            ActualizarEstadoMemoria();
            if (exito)
                MessageBox.Show("🏗️ Corredor nativo generado a partir del alineamiento y la rasante.", "INVIAS", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("No fue posible generar el Corridor automáticamente en este equipo (catálogo de subensambles no compatible). Los bordes de vía siguen disponibles como alineamientos de desfase nativos de la Pestaña 3. Vea el detalle en la Pestaña 4 (Memoria).", "INVIAS", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ==========================================
        // PESTAÑA 4: MEMORIA DESCRIPTIVA
        // ==========================================
        private void ActualizarEstadoMemoria()
        {
            if (TxtEstadoMemoria == null) return;
            TxtEstadoMemoria.Text =
                $"Estado: Planta {(_resultado.PlantaProcesada ? "OK" : "pendiente")} · " +
                $"Perfil {(_resultado.PerfilProcesado ? "OK" : "pendiente")} · " +
                $"Transversal {(_resultado.TransversalProcesado ? "OK" : "pendiente")} · " +
                $"Corredor {(_resultado.CorredorProcesado ? "OK" : "no generado")}. " +
                $"Advertencias: {_resultado.Advertencias.Count}.";
        }

        private void BtnGenerarMemoria_Click(object sender, RoutedEventArgs e)
        {
            if (!_resultado.PlantaProcesada && !_resultado.PerfilProcesado && !_resultado.TransversalProcesado)
            {
                MessageBox.Show("Aún no se ha procesado ningún cálculo. Complete al menos la Pestaña 1 (Planta) antes de generar la memoria.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Documento de Word (*.docx)|*.docx",
                FileName = $"Memoria_Descriptiva_{(_resultado.Parametros.NombreAlineamiento ?? "INVIAS")}.docx",
                DefaultExt = ".docx"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                GeneradorMemoriaWord.Generar(dialog.FileName, _resultado);
                MessageBox.Show($"📄 Memoria descriptiva generada correctamente en:\n{dialog.FileName}", "INVIAS", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"No se pudo generar la memoria descriptiva:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
