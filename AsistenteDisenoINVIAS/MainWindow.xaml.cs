using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Table = Autodesk.AutoCAD.DatabaseServices.Table;

namespace AsistenteDisenoINVIAS
{
    public partial class MainWindow : UserControl
    {
        public static ObjectId SelectedPolylineId = ObjectId.Null;

        // Longitudes de curva vertical REALMENTE insertadas por este mismo asistente,
        // indexadas por nombre de eje y abscisa (redondeada a 2 decimales). La pestaña
        // de exportación de memoria consulta primero este registro (dato exacto, sin
        // necesidad de reinterpretar la geometría de Civil3D) y solo recurre a la
        // lectura por reflexión sobre las entidades del perfil como último respaldo.
        private static readonly Dictionary<string, Dictionary<double, double>> RegistroCurvasVerticales = new Dictionary<string, Dictionary<double, double>>();

        private static void RegistrarCurvaVertical(string nombreEje, double station, double longitud)
        {
            if (!RegistroCurvasVerticales.TryGetValue(nombreEje, out Dictionary<double, double>? mapa))
            {
                mapa = new Dictionary<double, double>();
                RegistroCurvasVerticales[nombreEje] = mapa;
            }
            mapa[Math.Round(station, 2)] = longitud;
        }

        private static bool TryObtenerCurvaVerticalRegistrada(string nombreEje, double station, out double longitud)
        {
            longitud = 0.0;
            if (!RegistroCurvasVerticales.TryGetValue(nombreEje, out Dictionary<double, double>? mapa)) return false;
            if (mapa.TryGetValue(Math.Round(station, 2), out longitud)) return true;
            // Tolerancia de 0.05 m por si la abscisa se relee con un redondeo distinto
            foreach (var kv in mapa) { if (Math.Abs(kv.Key - station) < 0.05) { longitud = kv.Value; return true; } }
            return false;
        }

        // Lee la longitud total de una entidad de curva vertical del perfil, sin asumir
        // que siempre es una parábola simétrica: prueba varios nombres de propiedad
        // conocidos en la API de Civil3D (parabólica simétrica, asimétrica -con dos
        // ramas L1/L2- y circular), de modo que curvas dibujadas o editadas a mano con
        // cualquiera de estos tipos también se reporten correctamente en la memoria.
        private static bool TryLeerLongitudCurvaVertical(dynamic curveEntity, out double longitud)
        {
            longitud = 0.0;
            try { longitud = (double)curveEntity.Length; if (longitud > 0) return true; } catch { }
            try { longitud = (double)curveEntity.CurveLength; if (longitud > 0) return true; } catch { }
            try {
                double l1 = (double)curveEntity.CurveLengthIn;
                double l2 = (double)curveEntity.CurveLengthOut;
                longitud = l1 + l2;
                if (longitud > 0) return true;
            } catch { }
            try {
                double l1 = (double)curveEntity.LengthIn;
                double l2 = (double)curveEntity.LengthOut;
                longitud = l1 + l2;
                if (longitud > 0) return true;
            } catch { }
            return false;
        }

        private class CurveData
        {
            public string Elem { get; set; } = "";
            public double StartSt { get; set; }
            public double EndSt { get; set; }
            public double Radius { get; set; }
            public double S_max { get; set; }
            public bool RequiereSobreancho { get; set; }
            public double E_max { get; set; }
            public double Lt { get; set; }
            public bool IsRight { get; set; }
            public double Delta { get; set; }
            public double Length { get; set; }
            public double Tangent { get; set; }
            public double Vch { get; set; }
            public double Ftmax { get; set; }
            public double Rmin { get; set; }
            public double AsMax { get; set; }
            public double AsCalc { get; set; }
            public double LtMin { get; set; }
            public bool TieneEspiral { get; set; }
            public double LeEntrada { get; set; }
            public double LeSalida { get; set; }
            public double AEntrada { get; set; }
            public double ASalida { get; set; }
            public double AMinNormativo { get; set; }
            public double AMaxNormativo { get; set; }
        }

        private class PviData
        {
            public int Id { get; set; }
            public double Station { get; set; }
            public double Elevation { get; set; }
            public double GradeIn { get; set; }
            public double GradeOut { get; set; }
            public double A { get; set; }
            public double CurveLength { get; set; }
            public double K { get; set; }
            public string Type { get; set; } = "";
            public double KminNorma { get; set; }
            public double LminNorma { get; set; }
        }

        private class PIData
        {
            public int Id { get; set; }
            public double Station { get; set; }
            public double North { get; set; }
            public double East { get; set; }
            public double Delta { get; set; }
        }

        private class AlignElem
        {
            public string Elem { get; set; } = "";
            public string Type { get; set; } = "";
            public double StartSt { get; set; }
            public double EndSt { get; set; }
            public double Length { get; set; }
            public string Parameter { get; set; } = "";
        }

        private struct PviDefinition
        {
            public double Station { get; set; }
            public double Elevation { get; set; }
            public double CurveLength { get; set; }
        }

        public MainWindow()
        {
            InitializeComponent();
        }

        private ObjectId ObtenerEjeCentral(Database db, CivilDocument civilDoc)
        {
            ObjectIdCollection alignIds = civilDoc.GetAlignmentIds();
            if (alignIds.Count == 0) return ObjectId.Null;

            ObjectId resultId = ObjectId.Null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                for (int i = alignIds.Count - 1; i >= 0; i--)
                {
                    Alignment? al = tr.GetObject(alignIds[i], OpenMode.ForRead) as Alignment;
                    if (al != null && !al.Name.Contains("_Izdo_") && !al.Name.Contains("_Dcho_"))
                    {
                        resultId = alignIds[i];
                        break;
                    }
                }
                tr.Commit();
            }
            return resultId;
        }

        // Database.Clayer es un ObjectId (referencia a la LayerTableRecord de la capa
        // actual), NO un string — a diferencia de lo que su nombre podría sugerir. Los
        // métodos de creación de Civil3D (Alignment.Create, Profile.CreateFromSurface,
        // Profile.CreateByLayout) piden el nombre de capa como string; este helper resuelve
        // el ObjectId a su nombre real. Pasar db.Clayer directo donde se espera un string
        // es precisamente el error de compilación "Argumento N: no se puede convertir de
        // ObjectId a string" reportado por el usuario.
        private string ObtenerNombreCapaActual(Transaction tr, Database db)
        {
            try
            {
                LayerTableRecord? ltr = tr.GetObject(db.Clayer, OpenMode.ForRead) as LayerTableRecord;
                return ltr?.Name ?? "0";
            }
            catch { return "0"; }
        }

        // ==========================================
        // 🔹 SELECCIÓN EXPLÍCITA DE EJE Y PERFIL
        // Permite operar sobre CUALQUIER Alignment/Profile del dibujo, generado o no
        // por este asistente (p. ej. un eje trazado nativamente en Civil3D, con o sin
        // espirales de transición). Si el usuario no selecciona nada en los combos
        // CmbEjes/CmbPerfiles, se conserva como respaldo la heurística automática
        // anterior, para no romper el flujo de quien no necesita elegir.
        // ==========================================
        private void CmbEjes_Loaded(object sender, RoutedEventArgs e) { CargarEjesEnComboBox(); }
        private void CmbEjes_DropDownOpened(object sender, EventArgs e) { CargarEjesEnComboBox(); }

        private void CargarEjesEnComboBox()
        {
            if (CmbEjes == null) return;
            object? seleccionPrevia = (CmbEjes.SelectedItem as ComboBoxItem)?.Tag;
            CmbEjes.Items.Clear();
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            CivilDocument civilDoc = CivilApplication.ActiveDocument;
            ObjectIdCollection alignIds = civilDoc.GetAlignmentIds();
            if (alignIds.Count == 0) return;

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignId in alignIds)
                {
                    Alignment? al = tr.GetObject(alignId, OpenMode.ForRead) as Alignment;
                    // Se excluyen los ejes de desfase (_Izdo_/_Dcho_) creados por la Pestaña 3:
                    // no son ejes de diseño, son subproductos internos del sobreancho.
                    if (al != null && !al.Name.Contains("_Izdo_") && !al.Name.Contains("_Dcho_"))
                        CmbEjes.Items.Add(new ComboBoxItem { Content = al.Name, Tag = alignId });
                }
                tr.Commit();
            }

            if (seleccionPrevia is ObjectId prevId)
            {
                foreach (ComboBoxItem item in CmbEjes.Items)
                    if (item.Tag is ObjectId id && id == prevId) { CmbEjes.SelectedItem = item; break; }
            }
            if (CmbEjes.SelectedItem == null && CmbEjes.Items.Count > 0) CmbEjes.SelectedIndex = CmbEjes.Items.Count - 1;
        }

        private void CmbEjes_SelectionChanged(object sender, SelectionChangedEventArgs e) { CargarPerfilesEnComboBox(); }

        private void CmbPerfiles_DropDownOpened(object sender, EventArgs e) { CargarPerfilesEnComboBox(); }

        private void CargarPerfilesEnComboBox()
        {
            if (CmbPerfiles == null) return;
            CmbPerfiles.Items.Clear();

            ObjectId alignId = (CmbEjes?.SelectedItem as ComboBoxItem)?.Tag is ObjectId id ? id : ObjectId.Null;
            if (alignId == ObjectId.Null) return;

            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            int mejorIndice = -1; int maxPvis = 0;
            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                Alignment? alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment;
                if (alignment == null) { tr.Commit(); return; }

                foreach (ObjectId pId in alignment.GetProfileIds())
                {
                    Profile? p = tr.GetObject(pId, OpenMode.ForRead) as Profile;
                    if (p == null) continue;
                    string etiqueta = $"{p.Name} ({(p.ProfileType == ProfileType.EG ? "Terreno natural" : $"{p.PVIs.Count} PVI")})";
                    CmbPerfiles.Items.Add(new ComboBoxItem { Content = etiqueta, Tag = pId });
                    if (p.ProfileType != ProfileType.EG && p.PVIs.Count > maxPvis) { maxPvis = p.PVIs.Count; mejorIndice = CmbPerfiles.Items.Count - 1; }
                }
                tr.Commit();
            }

            if (mejorIndice >= 0) CmbPerfiles.SelectedIndex = mejorIndice;
            else if (CmbPerfiles.Items.Count > 0) CmbPerfiles.SelectedIndex = 0;
        }

        // Devuelve el eje elegido explícitamente en CmbEjes; si no hay selección
        // (combo vacío o control no presente en el XAML), recurre a la heurística
        // automática ObtenerEjeCentral para no romper flujos existentes.
        private ObjectId ObtenerEjeSeleccionado(Database db, CivilDocument civilDoc)
        {
            if (CmbEjes?.SelectedItem is ComboBoxItem item && item.Tag is ObjectId id && id != ObjectId.Null)
                return id;
            return ObtenerEjeCentral(db, civilDoc);
        }

        // Devuelve el perfil elegido explícitamente en CmbPerfiles para el eje dado;
        // si no hay selección, recurre a la heurística "perfil con más PVIs distinto
        // del terreno natural" usada anteriormente.
        private ObjectId ObtenerPerfilSeleccionado(Transaction tr, Alignment alignment)
        {
            if (CmbPerfiles?.SelectedItem is ComboBoxItem item && item.Tag is ObjectId id && id != ObjectId.Null)
                return id;

            ObjectId mejorId = ObjectId.Null; int maxPvis = 0;
            foreach (ObjectId pId in alignment.GetProfileIds())
            {
                Profile? p = tr.GetObject(pId, OpenMode.ForRead) as Profile;
                if (p != null && p.ProfileType != ProfileType.EG && p.PVIs.Count > maxPvis && p.PVIs.Count < 500)
                { mejorId = pId; maxPvis = p.PVIs.Count; }
            }
            return mejorId;
        }

        // ==========================================
        // 🔹 HELPERS COMPARTIDOS — PARÁMETROS NORMATIVOS INVIAS 2008
        // Cada valor está transcrito y verificado contra el Manual de Diseño
        // Geométrico de Carreteras INVIAS 2008 (tablas citadas en cada método).
        // ==========================================
        private double ObtenerVelocidadDiseno()
        {
            double vtr = 30.0;
            if (CmbVtr.SelectedItem is ComboBoxItem vtrItem && vtrItem.Content != null)
                double.TryParse(vtrItem.Content.ToString(), out vtr);
            return vtr;
        }

        private double ObtenerAnchoCarril()
        {
            double anchoCarril = 3.65;
            double.TryParse(TxtAnchoCarril.Text, out anchoCarril);
            return anchoCarril;
        }

        private double ObtenerLongitudVehiculoDiseno()
        {
            double lVehiculo = 10.5;
            if (CmbVehiculo.SelectedIndex == 0) lVehiculo = 6.1;
            else if (CmbVehiculo.SelectedIndex == 1) lVehiculo = 7.5;
            return lVehiculo;
        }

        // Numeral 3.2.2.1 INVIAS: en curvas circulares (sin espiral) con entretangencia
        // insuficiente, el peralte en el PC/PT debe quedar entre 60% y 80% del peralte total,
        // siempre que al menos 1/3 de la longitud de la curva quede con peralte total. El valor
        // por defecto (66.7%) es el punto medio del rango y equivale a la distribución clásica
        // 2/3 en tangente - 1/3 en curva; se deja configurable en el TextBox TxtPctTangente.
        private double ObtenerPorcentajeTransicionEnTangente()
        {
            double pct = 100.0 / 1.5; // 66.7%, valor por defecto si el control no existe o está vacío
            if (TxtPctTangente != null && double.TryParse(TxtPctTangente.Text, out double valor) && valor > 0)
                pct = valor;
            return Math.Min(80.0, Math.Max(60.0, pct));
        }

        // Tabla 3.1 INVIAS - Coeficiente de fricción transversal máxima (fTmáx)
        private double CalcularFriccionMaxima(double vtr)
        {
            int v = (int)Math.Round(vtr);
            if (v <= 20) return 0.35;
            if (v <= 30) return 0.28;
            if (v <= 40) return 0.23;
            if (v <= 50) return 0.19;
            if (v <= 60) return 0.17;
            if (v <= 70) return 0.15;
            if (v <= 80) return 0.14;
            if (v <= 90) return 0.13;
            if (v <= 100) return 0.12;
            if (v <= 110) return 0.11;
            if (v <= 120) return 0.09;
            return 0.08; // 130 km/h
        }

        // Numeral 3.1.3.2 INVIAS - Peralte máximo del proyecto (emáx):
        // 8% en carreteras Primarias/Secundarias, 6% en carreteras Terciarias.
        private double ObtenerPeralteMaximoProyecto(int catIdx) => catIdx == 3 ? 6.0 : 8.0;

        // ==========================================
        // 🔹 NUMERAL 3.1.3.5 INVIAS (MÉTODO 5 AASHTO) - PERALTE SEGÚN RADIO ADOPTADO
        // Tablas 3.4 (Primarias/Secundarias, emáx=8%) y 3.5 (Terciarias, emáx=6%), Radio
        // (Rc) en función de VCH y del peralte (e). Transcritas del Manual y verificadas:
        // el último renglón de cada columna (e = emáx) coincide EXACTAMENTE con el radio
        // mínimo ya calculado por fórmula en las Tablas 3.2/3.3 para esa misma VCH, en
        // los 15 puntos de cruce posibles (Primarias/Secundarias 40-130 km/h y Terciarias
        // 20-60 km/h) — la coincidencia numérica exacta en los 15 casos es la prueba de
        // que la transcripción es correcta.
        // ==========================================
        private static readonly double[] E_TABLA_8 = { 1.5, 2.0, 2.2, 2.4, 2.6, 2.8, 3.0, 3.2, 3.4, 3.6, 3.8, 4.0, 4.2, 4.4, 4.6, 4.8, 5.0, 5.2, 5.4, 5.6, 5.8, 6.0, 6.2, 6.4, 6.6, 6.8, 7.0, 7.2, 7.4, 7.6, 7.8, 8.0 };
        private static readonly Dictionary<int, double[]> RADIOS_EMAX8 = new Dictionary<int, double[]> {
            { 40,  new double[] { 784, 571, 512, 463, 421, 385, 354, 326, 302, 279, 259, 241, 224, 208, 192, 178, 163, 148, 136, 125, 115, 106, 98, 91, 85, 79, 73, 68, 62, 57, 52, 41 } },
            { 50,  new double[] { 1090, 791, 711, 644, 587, 539, 496, 458, 425, 395, 368, 344, 321, 301, 281, 263, 246, 229, 213, 198, 185, 172, 161, 151, 141, 132, 123, 115, 107, 99, 90, 73 } },
            { 60,  new double[] { 1490, 1090, 976, 885, 808, 742, 684, 633, 588, 548, 512, 479, 449, 421, 395, 371, 349, 328, 307, 288, 270, 253, 238, 224, 210, 198, 185, 174, 162, 150, 137, 113 } },
            { 70,  new double[] { 1970, 1450, 1300, 1190, 1080, 992, 916, 849, 790, 738, 690, 648, 608, 573, 540, 509, 480, 454, 429, 405, 382, 360, 340, 322, 304, 287, 270, 254, 237, 221, 202, 168 } },
            { 80,  new double[] { 2440, 1790, 1620, 1470, 1350, 1240, 1150, 1060, 988, 924, 866, 813, 766, 722, 682, 645, 611, 579, 549, 521, 494, 469, 445, 422, 400, 379, 358, 338, 318, 296, 273, 229 } },
            { 90,  new double[] { 2970, 2190, 1980, 1800, 1650, 1520, 1410, 1310, 1220, 1140, 1070, 1010, 948, 895, 847, 803, 762, 724, 689, 656, 625, 595, 567, 540, 514, 489, 464, 440, 415, 389, 359, 304 } },
            { 100, new double[] { 3630, 2680, 2420, 2200, 2020, 1860, 1730, 1610, 1500, 1410, 1320, 1240, 1180, 1110, 1050, 996, 947, 901, 859, 819, 781, 746, 713, 681, 651, 620, 591, 561, 531, 499, 462, 394 } },
            { 110, new double[] { 4180, 3090, 2790, 2550, 2340, 2160, 2000, 1870, 1740, 1640, 1540, 1450, 1380, 1300, 1240, 1180, 1120, 1070, 1020, 975, 933, 894, 857, 823, 789, 757, 724, 691, 657, 621, 579, 501 } },
            { 120, new double[] { 4900, 3640, 3290, 3010, 2760, 2550, 2370, 2220, 2080, 1950, 1840, 1740, 1650, 1570, 1490, 1420, 1360, 1300, 1250, 1200, 1150, 1100, 1060, 1020, 982, 948, 914, 879, 842, 803, 757, 667 } },
            { 130, new double[] { 5360, 4000, 3620, 3310, 3050, 2830, 2630, 2460, 2310, 2180, 2060, 1950, 1850, 1760, 1680, 1610, 1540, 1480, 1420, 1360, 1310, 1260, 1220, 1180, 1140, 1100, 1070, 1040, 998, 962, 919, 832 } },
        };

        private static readonly double[] E_TABLA_6 = { 1.5, 2.0, 2.2, 2.4, 2.6, 2.8, 3.0, 3.2, 3.4, 3.6, 3.8, 4.0, 4.2, 4.4, 4.6, 4.8, 5.0, 5.2, 5.4, 5.6, 5.8, 6.0 };
        private static readonly Dictionary<int, double[]> RADIOS_EMAX6 = new Dictionary<int, double[]> {
            { 20, new double[] { 194, 138, 122, 109, 97, 87, 78, 70, 61, 51, 42, 36, 31, 27, 24, 21, 19, 17, 15, 15, 15, 15 } },
            { 30, new double[] { 421, 299, 265, 236, 212, 190, 170, 152, 133, 113, 96, 82, 72, 63, 56, 50, 45, 40, 36, 32, 28, 21 } },
            { 40, new double[] { 738, 525, 465, 415, 372, 334, 300, 269, 239, 206, 177, 155, 136, 121, 108, 97, 88, 79, 71, 63, 56, 43 } },
            { 50, new double[] { 1050, 750, 668, 599, 540, 488, 443, 402, 364, 329, 294, 261, 234, 210, 190, 172, 156, 142, 128, 115, 102, 79 } },
            { 60, new double[] { 1440, 1030, 919, 825, 746, 676, 615, 561, 511, 465, 422, 380, 343, 311, 283, 258, 235, 214, 195, 176, 156, 123 } },
        };

        // Peralte (e) requerido para una curva de Radio "radio" y velocidad "vch", interpolando
        // en la Tabla 3.4 o 3.5 según la categoría. Sustituye la práctica de forzar emáx en
        // todas las curvas: una curva de Radio bastante mayor al mínimo requiere menos peralte
        // (y por tanto una transición de peralte más corta, numeral 3.2.2).
        private double ObtenerPeraltePorRadio(double vch, double radio, int catIdx)
        {
            bool terciaria = catIdx == 3;
            Dictionary<int, double[]> tabla = terciaria ? RADIOS_EMAX6 : RADIOS_EMAX8;
            double[] eArr = terciaria ? E_TABLA_6 : E_TABLA_8;
            double eMax = ObtenerPeralteMaximoProyecto(catIdx);

            int vKey = tabla.Keys.First();
            double mejorDist = double.MaxValue;
            foreach (int k in tabla.Keys) { double d = Math.Abs(k - vch); if (d < mejorDist) { mejorDist = d; vKey = k; } }
            double[] r = tabla[vKey];

            if (radio >= r[0]) return 2.0; // más holgado que el límite de 1.5% de la tabla: basta el bombeo normal (2%)
            if (radio <= r[r.Length - 1]) return eMax; // en o por debajo del radio mínimo: usar el peralte máximo

            for (int i = 0; i < r.Length - 1; i++)
            {
                if (radio <= r[i] && radio >= r[i + 1])
                {
                    // Algunas columnas (p. ej. VCH=20 km/h en la Tabla 3.5) se saturan en el radio
                    // mínimo práctico y repiten el mismo valor en renglones consecutivos; en ese
                    // tramo no hay nada que interpolar y se adopta directamente el peralte mayor.
                    if (r[i] == r[i + 1]) return eArr[i + 1];

                    double t = (r[i] - radio) / (r[i] - r[i + 1]); // 0 en r[i], 1 en r[i+1]
                    return eArr[i] + t * (eArr[i + 1] - eArr[i]);
                }
            }
            return eMax; // respaldo defensivo, no debería alcanzarse
        }

        // Numeral 3.1.3.4 INVIAS - Radio mínimo de curvatura (RCmín), Tablas 3.2/3.3:
        // RCmín = VCH² / (127 × (emáx + fTmáx))
        private double CalcularRadioMinimo(double vtr, int catIdx)
        {
            double fMax = CalcularFriccionMaxima(vtr);
            double eMax = ObtenerPeralteMaximoProyecto(catIdx) / 100.0;
            double rMinCalculado = Math.Pow(vtr, 2) / (127.0 * (eMax + fMax));
            // Se redondea al entero superior: al tratarse de un mínimo, redondear hacia
            // arriba nunca resulta insuficiente (a diferencia del redondeo al más cercano
            // que usa la Tabla 3.2/3.3 del Manual, que en varios casos redondea hacia abajo,
            // p. ej. 113.4→113 o 501.5→501). El valor así obtenido puede diferir en como
            // máximo 1 m del impreso en el Manual, siempre por el lado conservador.
            return Math.Ceiling(rMinCalculado);
        }

        // Tabla 3.6 INVIAS - Pendiente relativa máxima de la rampa de peraltes (Δs máx, %)
        private double ObtenerDeltaSMaximo(double vtr)
        {
            int v = (int)Math.Round(vtr);
            if (v <= 20) return 1.35;
            if (v <= 30) return 1.28;
            if (v <= 40) return 0.96;
            if (v <= 50) return 0.77;
            if (v <= 60) return 0.60;
            if (v <= 70) return 0.55;
            if (v <= 80) return 0.50;
            if (v <= 90) return 0.47;
            if (v <= 100) return 0.44;
            if (v <= 110) return 0.41;
            return 0.38; // 120 y 130 km/h
        }

        // Numeral 3.2.2 INVIAS - Longitud de transición del peralte:
        // L = a × (ef - ei) / Δs ; en curvas simples/espiralizadas ei = 0, por lo que
        // ef = emáx del proyecto. NO existe en el Manual un segundo término que dependa
        // de Vtr; usar solo esta expresión evita transiciones sobredimensionadas.
        private double CalcularLongitudTransicionPeralte(double anchoCarrilGira, double peralteFinal, double deltaSMax)
        {
            return (anchoCarrilGira * peralteFinal) / deltaSMax;
        }

        // Tabla 5.5 INVIAS - Distancia entre el parachoques delantero y el eje trasero (L)
        // para el cálculo del sobreancho de vehículos rígidos (numeral 5.4.1.1). El mapeo de
        // índices sigue el orden del ComboBox CmbVehiculo (0=C2, 1=C3, 2=T3-S2); T3-S2 es un
        // vehículo ARTICULADO y no usa este L: para él, RequiereSobreanchoCurva/CalcularSobreancho
        // se sustituyen por CalcularSobreanchoArticulado (numeral 5.4.1.2). Verificado contra la
        // Tabla 5.5 impresa: L = b (volado delantero) + a (distancia entre ejes).
        private double ObtenerLongitudVehiculoSobreancho()
        {
            if (CmbVehiculo.SelectedIndex == 0) return 8.00;  // Camión de dos ejes (C2): a=6.60, b=1.40
            return 7.80; // Camión de tres ejes o dobletroque (C3): a=6.55, b=1.25
        }

        // Numeral 5.4.1.1 INVIAS: el sobreancho está limitado a curvas de Radio menor a
        // 160 m, y no se requiere si la calzada en tangente supera 7.0 m, salvo curvas
        // con ángulo de deflexión mayor a 120°.
        private bool RequiereSobreanchoCurva(double radio, double anchoCalzadaTangente, double deltaGrados)
        {
            if (radio >= 160.0) return false;
            if (anchoCalzadaTangente > 7.0 && deltaGrados <= 120.0) return false;
            return true;
        }

        // Numeral 5.4.1.1 INVIAS: S = n × (Rc - √(Rc² - L²))  [Figura 5.3 / pág. 155]
        // Válida SOLO para vehículos rígidos (C2, C3). Para el articulado 3S2 usar
        // CalcularSobreanchoArticulado.
        private double CalcularSobreancho(double radio, double lVehiculoSobreancho, double nCarriles = 2.0)
        {
            double b = Math.Sqrt(Math.Max(0.0, radio * radio - lVehiculoSobreancho * lVehiculoSobreancho));
            return nCarriles * (radio - b);
        }

        // Tabla 5.6 INVIAS (numeral 5.4.1.2, pág. 157) - Dimensiones del vehículo articulado
        // 3S2 representativo del parque automotor colombiano, según el esquema A/L1/L2/L3/u
        // del Manual (unidad tractora + semirremolque de dos ejes).
        private const double VEH_3S2_A = 1.22, VEH_3S2_L1 = 5.95, VEH_3S2_L2 = 0.0, VEH_3S2_L3 = 12.97, VEH_3S2_U = 2.59;

        // Tabla 5.7 INVIAS (pág. 159) - Espacio lateral de seguridad C en función del ancho de
        // calzada en tangente (AT). Los 3 puntos tabulados (6.00→0.60, 6.60→0.75, 7.20→0.90)
        // son equiespaciados, así que se interpolan linealmente. El Manual solo autoriza
        // INTERPOLAR ("para calzada de ancho diferente se puede encontrar el valor por
        // interpolación"), no extrapolar, así que AT se acota (clamp) al rango [6.00, 7.20]
        // antes de interpolar.
        private double ObtenerEspacioLateralSeguridad(double anchoCalzadaTangente)
        {
            double at = Math.Max(6.00, Math.Min(7.20, anchoCalzadaTangente));
            return 0.60 + (at - 6.00) / 0.60 * 0.15;
        }

        // Numeral 5.4.1.2 INVIAS (metodología AASHTO 2004) - Sobreancho requerido por el
        // vehículo articulado 3S2, en reemplazo de la fórmula de cuerpo rígido del numeral
        // 5.4.1.1 (que no es válida para un vehículo con articulación). S = Ac - At, con:
        //   U  = u + Rc − √(Rc² − (L1+L2+L3)²)          (ancho ocupado por el vehículo en curva)
        //   FA = √(Rc² + A·(2·L1 + A)) − Rc               (avance del voladizo delantero)
        //   Z  = 0.1 × √(VCH / Rc)                        (sobreancho adicional de seguridad)
        //   C  = espacio lateral de seguridad (Tabla 5.7)
        //   Ac = n × (U + C) + (n − 1) × FA + Z
        private double CalcularSobreanchoArticulado(double radio, double vch, double anchoCalzadaTangente, double nCarriles = 2.0)
        {
            double sumaL = VEH_3S2_L1 + VEH_3S2_L2 + VEH_3S2_L3;
            double u = VEH_3S2_U + radio - Math.Sqrt(Math.Max(0.0, radio * radio - sumaL * sumaL));
            double fa = Math.Sqrt(Math.Max(0.0, radio * radio + VEH_3S2_A * (2.0 * VEH_3S2_L1 + VEH_3S2_A))) - radio;
            double z = radio > 0 ? 0.1 * Math.Sqrt(vch / radio) : 0.0;
            double c = ObtenerEspacioLateralSeguridad(anchoCalzadaTangente);
            double ac = nCarriles * (u + c) + (nCarriles - 1.0) * fa + z;
            return Math.Max(0.0, ac - anchoCalzadaTangente);
        }

        // ==========================================
        // 🔹 SOPORTE DE ESPIRALES (CLOTOIDES) — numerales 3.1.1.2 y 3.2.2.2 INVIAS
        // Permite leer ejes trazados con Civil3D nativo (no solo los generados por la
        // Pestaña 1, que nunca inserta espirales). Las entidades compuestas del tipo
        // Espiral-Curva-Espiral no tienen un nombre de clase único y estable en todas
        // las versiones de la API, así que se descomponen con el patrón estándar
        // SubEntityCount/indexador (documentado por Autodesk) y se leen sus propiedades
        // por reflexión (dynamic), en vez de asumir un nombre de tipo concreto.
        // ==========================================
        private IEnumerable<AlignmentEntity> AplanarEntidadAlineamiento(AlignmentEntity entity)
        {
            if (entity is AlignmentLine || entity is AlignmentArc)
            {
                yield return entity;
                yield break;
            }

            List<AlignmentEntity> subs = new List<AlignmentEntity>();
            try
            {
                dynamic d = entity;
                int n = (int)d.SubEntityCount;
                for (int i = 0; i < n; i++)
                {
                    AlignmentEntity sub = (AlignmentEntity)d[i];
                    subs.Add(sub);
                }
            }
            catch { }

            if (subs.Count > 0)
            {
                foreach (var sub in subs)
                    foreach (var leaf in AplanarEntidadAlineamiento(sub))
                        yield return leaf;
            }
            else
            {
                // No se pudo descomponer (o ya es una hoja no Línea/Arco, típicamente
                // una Espiral): se entrega tal cual para no perderla del listado.
                yield return entity;
            }
        }

        // Lee los datos de una entidad "hoja" que no es Línea ni Arco (se asume Espiral)
        // por reflexión, sin depender del nombre exacto de su clase.
        private bool TryExtraerDatosEspiral(AlignmentEntity entity, out double longitud, out double parametroA, out double startSt, out double endSt)
        {
            longitud = 0; parametroA = 0; startSt = 0; endSt = 0;
            if (entity is AlignmentLine || entity is AlignmentArc) return false;
            try
            {
                dynamic d = entity;
                try { longitud = (double)d.Length; } catch { }
                try { parametroA = (double)d.A; } catch { }
                try { startSt = (double)d.StartStation; } catch { }
                try { endSt = (double)d.EndStation; } catch { }
                return longitud > 0;
            }
            catch { return false; }
        }

        // ==========================================
        // 🔹 NUMERAL 3.3 INVIAS - LONGITUD DE LA CURVA ESPIRAL (parámetro A)
        // El parámetro mínimo de diseño es la ENVOLVENTE SUPERIOR (el mayor) de los
        // tres criterios I, II y III; el parámetro adoptado (A = √(Rc·Le)) debe quedar
        // entre ese mínimo y el máximo Amáx = 1.1·Rc.
        // ==========================================

        // Tabla 3.7 INVIAS - Variación de la aceleración centrífuga (J, m/s³)
        private double ObtenerVariacionAceleracionCentrifuga(double vtr)
        {
            int v = (int)Math.Round(vtr);
            if (v <= 70) return 0.7;
            if (v <= 90) return 0.6;
            if (v <= 110) return 0.5;
            return 0.4; // 120-130 km/h
        }

        // Criterio I (numeral 3.3.1): variación uniforme de la aceleración centrífuga (J),
        // no compensada por el peralte. e y VCH según convención del Manual (e en %, VCH en km/h).
        private double CalcularAMinDinamico(double vch, double rc, double e)
        {
            double j = ObtenerVariacionAceleracionCentrifuga(vch);
            double interior = (vch * vch / rc) - (1.27 * e);
            if (interior <= 0) return 0.0; // el radio ya es tan holgado que este criterio no exige espiral
            return Math.Sqrt((vch * rc / (46.656 * j)) * interior);
        }

        // Criterio II (numeral 3.3.1): limitación por transición del peralte, en función de
        // Δs (Tabla 3.6) y "a" (distancia del eje de giro al borde de calzada; se adopta el
        // ancho de carril del proyecto, consistente con la Lt de peralte ya calculada).
        private double CalcularAMinPeralte(double rc, double e, double a, double deltaSMax)
        {
            if (deltaSMax <= 0) return 0.0;
            return Math.Sqrt(rc * (e * a) / deltaSMax);
        }

        // Criterio III (numeral 3.3.1): condición de percepción y estética — el mayor entre
        // III.1 (disloque mínimo de 0.25 m) y III.2 (ángulo de giro mínimo de la espiral, 3°).
        private double CalcularAMinEstetico(double rc)
        {
            double aIII1 = Math.Pow(6.0 * Math.Pow(rc, 3), 0.25);   // ⁴√(24·ΔRmín·Rc³), con ΔRmín = 0.25 m
            double aIII2 = 0.3236 * rc;                              // ángulo de giro mínimo θe ≥ 3°
            return Math.Max(aIII1, aIII2);
        }

        // Parámetro A mínimo normativo = envolvente superior de los tres criterios.
        private double CalcularAMinimoEspiral(double vch, double rc, double e, double a, double deltaSMax)
        {
            double aI = CalcularAMinDinamico(vch, rc, e);
            double aII = CalcularAMinPeralte(rc, e, a, deltaSMax);
            double aIII = CalcularAMinEstetico(rc);
            return Math.Max(aI, Math.Max(aII, aIII));
        }

        // Numeral 3.3.2 INVIAS - Parámetro A máximo: Amáx = 1.1 × Rc
        private double CalcularAMaximoEspiral(double rc) => 1.1 * rc;

        // Numeral 3.3.1 INVIAS - Longitud de espiral (Le) que produce exactamente el
        // parámetro A mínimo normativo (envolvente de los criterios I/II/III): Le = Amín²/Rc.
        // Es el Le más corto que ya cumple el Manual para esta curva (criterio conservador,
        // consistente con "hazlo tú, limpio y seguro": ni de más ni de menos que el mínimo
        // exigido). Se acota para no superar tampoco el máximo normativo (numeral 3.3.2).
        private double ObtenerLongitudEspiralNormativa(double vch, double radio, double eCurva, double anchoCarril, double deltaSMax)
        {
            double aMin = CalcularAMinimoEspiral(vch, radio, eCurva, anchoCarril, deltaSMax);
            double aMax = CalcularAMaximoEspiral(radio);
            double leParaAMin = (aMin * aMin) / radio;
            double leMaximo = (aMax * aMax) / radio;
            return Math.Min(leParaAMin, leMaximo);
        }

        // Numeral 3.7 INVIAS: el diseñador SOLO puede omitir la espiral de transición
        // cuando el Radio de la curva sea superior a 1000 m, sin importar la categoría de
        // la vía ni la Velocidad Específica (VCH). Por debajo de ese umbral la curva DEBE
        // llevar espiral. Este método intenta convertir automáticamente, en el sitio, la
        // curva circular fija (creada por Alignment.Create a partir de la polilínea) en una
        // espiral-curva-espiral con longitud Le entrada/salida igual al mínimo normativo.
        //
        // ADVERTENCIA DE IMPLEMENTACIÓN: la firma exacta del método de la API de Civil3D
        // para "insertar una espiral-curva-espiral entre dos tangentes existentes" varía
        // entre versiones del producto. El candidato principal (AddFreeSCS) fue verificado
        // por el usuario contra la documentación real de SU ensamblado; los otros dos son
        // conjeturas de respaldo. Como no se conocen los nombres exactos de los valores de
        // los enums SpiralParamType/SpiralType en esta versión, se ubican por reflexión
        // sobre la propia firma del método (Enum.GetNames), buscando el que mejor coincida
        // por nombre, en vez de referenciar el tipo del enum directamente en el código (lo
        // cual arriesgaría un error de COMPILACIÓN si el nombre exacto no es el supuesto).
        // Si ningún candidato funciona en la versión de Civil3D instalada, la curva
        // permanece como círculo puro (que ya cumple el Radio mínimo, así que el diseño
        // sigue siendo válido, solo que sin espiral) y el llamador debe reportar el Le
        // exacto para inserción manual — nunca se debe dejar de informar el requisito
        // normativo pendiente.
        // NOTA DE ARQUITECTURA (corrige el fallo "0 espirales insertadas" observado por el
        // usuario): esta versión recibe idAntes/idDespues como el EntityId REAL devuelto por
        // Civil3D al crear cada tangente (ver TryAgregarLineaFija), no como el índice posicional
        // de la entidad dentro de la colección. AddFreeSCS opera sobre identificadores estables
        // de entidad, no sobre posición dentro de la colección — usar el índice (hipótesis
        // anterior) es la causa más probable de que la inserción automática fallara siempre,
        // incluso con el nombre de método correcto.
        private bool TryInsertarEspiralAutomatica(dynamic entities, int idAntes, int idDespues, double radio, double le, bool isGreaterThan180, out string ultimoError)
        {
            ultimoError = "";
            object entidadesObj = entities;
            Type tEntidades = entidadesObj.GetType();

            // Candidato 1 (verificado): AddFreeSCS(int previousEntityId, int nextEntityId,
            // double spiral1Param, double spiral2Param, SpiralParamType spType, double radius,
            // bool isGreaterThan180, SpiralType spiralDefinition).
            try
            {
                System.Reflection.MethodInfo? mi = tEntidades
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "AddFreeSCS" && m.GetParameters().Length == 8);

                if (mi == null)
                {
                    ultimoError = "No existe AddFreeSCS(8 parámetros) en esta versión.";
                }
                else
                {
                    var parametros = mi.GetParameters();
                    Type tSpiralParamType = parametros[4].ParameterType;
                    Type tSpiralType = parametros[7].ParameterType;
                    object? spParamValue = BuscarValorDeEnumPorNombre(tSpiralParamType, "Length", "Longitud", "Le");
                    object? spTypeValue = BuscarValorDeEnumPorNombre(tSpiralType, "Clothoid", "Clotoide");

                    if (spParamValue == null || spTypeValue == null)
                    {
                        ultimoError = $"AddFreeSCS existe pero no se identificó el valor de enum adecuado (SpiralParamType: {string.Join(",", Enum.GetNames(tSpiralParamType))}; SpiralType: {string.Join(",", Enum.GetNames(tSpiralType))}).";
                    }
                    else
                    {
                        object[] args = new object[] {
                            idAntes, idDespues, le, le, spParamValue, radio, isGreaterThan180, spTypeValue
                        };
                        mi.Invoke(entidadesObj, args);
                        return true;
                    }
                }
            }
            catch (System.Exception ex1) { ultimoError = ObtenerMensajeReal(ex1); }

            // Candidato de respaldo: en vez de adivinar más nombres, se buscan por reflexión
            // TODOS los métodos que contengan "Spiral" (distintos de AddFreeSCS) cuyos dos
            // primeros parámetros sean enteros (mismo patrón id-anterior/id-siguiente) y se
            // arma la lista de argumentos según el TIPO real de cada parámetro restante
            // (double → Le/Radio en ese orden, bool → isGreaterThan180, enum → se resuelve por
            // nombre; si ninguna pista coincide, se prueban TODOS los valores del enum en vez
            // de descartar la sobrecarga — el diagnóstico real mostró que SpiralCurveType es
            // InCurve/OutCurve, algo que ninguna pista de "tipo de espiral" podía adivinar).
            // Ningún nombre de método o de enum se referencia de forma directa/tipada, así que
            // un intento fallido nunca puede romper la compilación.
            var candidatosGenericos = tEntidades
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(m => m.Name.IndexOf("Spiral", StringComparison.OrdinalIgnoreCase) >= 0 && m.Name != "AddFreeSCS")
                .ToList();
            foreach (var mi in candidatosGenericos)
            {
                var ps = mi.GetParameters();
                if (ps.Length < 4 || ps[0].ParameterType != typeof(int) || ps[1].ParameterType != typeof(int)) continue;

                object?[] argsBase = new object?[ps.Length];
                argsBase[0] = idAntes; argsBase[1] = idDespues;
                int doublesUsados = 0;
                bool descartar = false;
                List<int> posicionesEnum = new List<int>();
                List<object[]> candidatosPorPosicion = new List<object[]>();
                for (int k = 2; k < ps.Length; k++)
                {
                    Type pt = ps[k].ParameterType;
                    if (pt == typeof(double)) { argsBase[k] = doublesUsados == 1 ? radio : le; doublesUsados++; }
                    else if (pt == typeof(bool)) argsBase[k] = isGreaterThan180;
                    else if (pt.IsEnum)
                    {
                        posicionesEnum.Add(k);
                        candidatosPorPosicion.Add(ObtenerCandidatosDeEnum(pt, "Clothoid", "Clotoide", "Length", "Longitud"));
                    }
                    else { descartar = true; break; }
                }
                if (descartar) continue;

                int totalCombinaciones = candidatosPorPosicion.Count == 0 ? 1 : candidatosPorPosicion.Select(v => v.Length).Aggregate(1, (a, b) => a * b);
                for (int combo = 0; combo < totalCombinaciones; combo++)
                {
                    object?[] args = (object?[])argsBase.Clone();
                    int resto = combo;
                    for (int idx = 0; idx < posicionesEnum.Count; idx++)
                    {
                        var candidatosEnum = candidatosPorPosicion[idx];
                        args[posicionesEnum[idx]] = candidatosEnum[resto % candidatosEnum.Length];
                        resto /= candidatosEnum.Length;
                    }
                    try
                    {
                        mi.Invoke(entidadesObj, args);
                        return true;
                    }
                    catch (System.Exception exGen) { ultimoError = ObtenerMensajeReal(exGen); }
                }
            }

            return false;
        }

        // Extrae por reflexión el EntityId real de una entidad de alineamiento recién creada
        // (p. ej. la que devuelve AddFixedLine). Es el identificador que exigen AddFreeSCS y
        // AddFreeCurveBetweenTangents, distinto del índice posicional dentro de la colección.
        private int? ObtenerEntityIdDeEntidad(object? entidad)
        {
            if (entidad == null) return null;
            var prop = entidad.GetType().GetProperty("EntityId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop != null && prop.GetValue(entidad) is int id) return id;
            return null;
        }

        // Crea, por reflexión, la tangente recta fija entre dos vértices de la polilínea
        // original (AddFixedLine). Se busca por nombre y aridad, no por firma tipada exacta,
        // porque Point2d/Point3d varían según la sobrecarga real de esta versión de Civil3D.
        private bool TryAgregarLineaFija(dynamic entities, Point2d p1, Point2d p2, out int entityId, out string error)
        {
            entityId = -1; error = "";
            object entidadesObj = entities;
            Type tEntidades = entidadesObj.GetType();
            var candidatos = tEntidades
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(m => m.Name == "AddFixedLine" && m.GetParameters().Length == 2)
                .ToList();

            if (candidatos.Count == 0) { error = "No existe AddFixedLine(2 parámetros) en esta versión."; return false; }

            foreach (var mi in candidatos)
            {
                try
                {
                    var parametros = mi.GetParameters();
                    object arg1, arg2;
                    if (parametros[0].ParameterType == typeof(Point3d))
                    {
                        arg1 = new Point3d(p1.X, p1.Y, 0.0);
                        arg2 = new Point3d(p2.X, p2.Y, 0.0);
                    }
                    else
                    {
                        arg1 = p1;
                        arg2 = p2;
                    }
                    object? resultado = mi.Invoke(entidadesObj, new object[] { arg1, arg2 });
                    int? id = ObtenerEntityIdDeEntidad(resultado);
                    if (id.HasValue) { entityId = id.Value; return true; }
                    error = "AddFixedLine se ejecutó pero la entidad devuelta no expone EntityId.";
                }
                catch (System.Exception ex) { error = ObtenerMensajeReal(ex); }
            }
            return false;
        }

        // Inserta, por reflexión, una curva circular simple (sin espiral) entre dos tangentes
        // ya existentes — caso Radio > 1000 m del numeral 3.7 (o modo Vía urbana). NUNCA se
        // había confirmado realmente que "AddFreeCurveBetweenTangents(4 parámetros)" funcione
        // en la instalación del usuario: en el modo Óptimo, casi todas las curvas del eje de
        // prueba caían por debajo de 1000 m y se insertaban como espiral, así que esta ruta
        // apenas se ejercitó. Ante el fallo total reportado en el modo Vía urbana (0 de 3
        // curvas insertadas, cuando el modo Compacto —con radio igual o menor y espiral, que
        // exige MÁS tangente disponible que la curva pura— sí insertó espirales en ese mismo
        // eje), se amplía la búsqueda: primero el nombre exacto con CUALQUIER número de
        // parámetros (no solo 4, por si la firma real difiere), acumulando todos los intentos
        // fallidos; y si ninguno sirve, un candidato genérico que busca cualquier método cuyo
        // nombre contenga "Curve" Y "Tangent" con el mismo patrón de dos enteros iniciales
        // (id-anterior/id-siguiente) ya usado para espirales — así, si el nombre real difiere
        // ligeramente, igual se encuentra sin arriesgar un error de compilación.
        private bool TryInsertarCurvaLibre(dynamic entities, int idAntes, int idDespues, double radio, bool isGreaterThan180, out string error)
        {
            List<string> errores = new List<string>();
            object entidadesObj = entities;
            Type tEntidades = entidadesObj.GetType();

            // Candidato prioritario (confirmado real por el diagnóstico del usuario, no una
            // conjetura): AddFreeCurve(int previousEntityId, int nextEntityId, double
            // paramValue, CurveParamType paramType, bool isGreaterThan180, CurveType curveType).
            // 'AddFreeCurveBetweenTangents' (el nombre que se asumía antes) no existe en esta
            // versión — el real ni siquiera contiene "Tangent", por eso la búsqueda anterior
            // nunca lo encontraba. CurveParamType.Radius es una coincidencia EXACTA de nombre
            // (no una pista aproximada). CurveType solo tiene los valores Compound/Reverse en
            // esta versión — ninguno significa claramente "curva simple" — así que se prueban
            // ambos con ObtenerCandidatosDeEnum en vez de adivinar uno solo y descartar si falla.
            var candidatosAddFreeCurve = tEntidades
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(m => m.Name == "AddFreeCurve")
                .OrderBy(m => m.GetParameters().Length)
                .ToList();
            foreach (var mi in candidatosAddFreeCurve)
            {
                var ps = mi.GetParameters();
                if (ps.Length < 4 || ps[0].ParameterType != typeof(int) || ps[1].ParameterType != typeof(int)) { errores.Add(FormatearFirma("AddFreeCurve", ps) + ": firma no reconocible (se esperaban dos int iniciales)."); continue; }

                object?[] argsBase = new object?[ps.Length];
                argsBase[0] = idAntes; argsBase[1] = idDespues;
                bool descartarAFC = false;
                bool radioAsignado = false;
                List<int> posicionesEnumAFC = new List<int>();
                List<object[]> candidatosPorPosicionAFC = new List<object[]>();
                for (int k = 2; k < ps.Length; k++)
                {
                    Type pt = ps[k].ParameterType;
                    if (pt == typeof(Point3d)) { descartarAFC = true; break; } // variante con punto de paso: no aplica, no tenemos ese punto
                    else if (pt == typeof(double) && !radioAsignado) { argsBase[k] = radio; radioAsignado = true; }
                    else if (pt == typeof(bool)) argsBase[k] = isGreaterThan180;
                    else if (pt.IsEnum)
                    {
                        posicionesEnumAFC.Add(k);
                        candidatosPorPosicionAFC.Add(ObtenerCandidatosDeEnum(pt, "Radius"));
                    }
                    else { descartarAFC = true; break; }
                }
                if (descartarAFC) { errores.Add(FormatearFirma("AddFreeCurve", ps) + ": parámetro no reconocible o requiere un punto de paso no disponible."); continue; }

                int totalCombinacionesAFC = candidatosPorPosicionAFC.Count == 0 ? 1 : candidatosPorPosicionAFC.Select(v => v.Length).Aggregate(1, (a, b) => a * b);
                for (int combo = 0; combo < totalCombinacionesAFC; combo++)
                {
                    object?[] args = (object?[])argsBase.Clone();
                    int resto = combo;
                    for (int idx = 0; idx < posicionesEnumAFC.Count; idx++)
                    {
                        var candidatosEnum = candidatosPorPosicionAFC[idx];
                        args[posicionesEnumAFC[idx]] = candidatosEnum[resto % candidatosEnum.Length];
                        resto /= candidatosEnum.Length;
                    }
                    try
                    {
                        mi.Invoke(entidadesObj, args);
                        error = "";
                        return true;
                    }
                    catch (System.Exception ex) { errores.Add($"{FormatearFirma("AddFreeCurve", ps)}: {ObtenerMensajeReal(ex)}"); }
                }
            }

            var candidatosExactos = tEntidades
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(m => m.Name == "AddFreeCurveBetweenTangents")
                .OrderBy(m => m.GetParameters().Length)
                .ToList();

            foreach (var mi in candidatosExactos)
            {
                var ps = mi.GetParameters();
                if (ps.Length < 4 || ps[0].ParameterType != typeof(int) || ps[1].ParameterType != typeof(int)) { errores.Add(FormatearFirma("AddFreeCurveBetweenTangents", ps) + ": firma no reconocible (se esperaban dos int iniciales)."); continue; }
                bool descartar = false;
                object?[] args = new object?[ps.Length];
                args[0] = idAntes; args[1] = idDespues;
                int doublesUsados = 0;
                for (int k = 2; k < ps.Length; k++)
                {
                    Type pt = ps[k].ParameterType;
                    if (pt == typeof(double)) { args[k] = radio; doublesUsados++; }
                    else if (pt == typeof(bool)) args[k] = isGreaterThan180;
                    else if (pt.IsEnum)
                    {
                        object? val = BuscarValorDeEnumPorNombre(pt, "Default", "Standard", "None");
                        if (val == null) { descartar = true; break; }
                        args[k] = val;
                    }
                    else { descartar = true; break; }
                }
                if (descartar) { errores.Add(FormatearFirma("AddFreeCurveBetweenTangents", ps) + ": parámetro no reconocible."); continue; }
                try
                {
                    mi.Invoke(entidadesObj, args);
                    error = "";
                    return true;
                }
                catch (System.Exception ex) { errores.Add($"{FormatearFirma("AddFreeCurveBetweenTangents", ps)}: {ObtenerMensajeReal(ex)}"); }
            }

            var candidatosGenericos = tEntidades
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(m => m.Name != "AddFreeCurveBetweenTangents"
                    && m.Name.IndexOf("Curve", StringComparison.OrdinalIgnoreCase) >= 0
                    && m.Name.IndexOf("Tangent", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            foreach (var mi in candidatosGenericos)
            {
                var ps = mi.GetParameters();
                if (ps.Length < 3 || ps[0].ParameterType != typeof(int) || ps[1].ParameterType != typeof(int)) continue;
                bool descartar = false;
                object?[] args = new object?[ps.Length];
                args[0] = idAntes; args[1] = idDespues;
                for (int k = 2; k < ps.Length; k++)
                {
                    Type pt = ps[k].ParameterType;
                    if (pt == typeof(double)) args[k] = radio;
                    else if (pt == typeof(bool)) args[k] = isGreaterThan180;
                    else if (pt.IsEnum)
                    {
                        object? val = BuscarValorDeEnumPorNombre(pt, "Default", "Standard", "None");
                        if (val == null) { descartar = true; break; }
                        args[k] = val;
                    }
                    else { descartar = true; break; }
                }
                if (descartar) continue;
                try
                {
                    mi.Invoke(entidadesObj, args);
                    error = "";
                    return true;
                }
                catch (System.Exception ex) { errores.Add($"{FormatearFirma(mi.Name, ps)}: {ObtenerMensajeReal(ex)}"); }
            }

            error = errores.Count > 0 ? string.Join(" | ", errores) : "No se encontró ningún método de curva libre entre tangentes ('AddFreeCurve', 'AddFreeCurveBetweenTangents' ni una variante con 'Curve' y 'Tangent' en el nombre) en esta versión.";
            return false;
        }

        // Crea, por reflexión, un alineamiento VACÍO (sin PolylineOptions) para construirlo
        // PI por PI: se buscan las sobrecargas estáticas de Alignment.Create que NO reciban un
        // parámetro de tipo PolylineOptions (esa es la ruta de "calcar la polilínea de un
        // tirón" que estamos evitando) ni CorridorFeatureLine (esa sobrecarga crea el
        // alineamiento A PARTIR de un corredor, no aplica aquí). Cualquier otro parámetro de
        // un tipo no reconocido (p. ej. una clase de opciones propia de esta versión) se
        // intenta instanciar con su constructor sin argumentos, en vez de descartar la
        // sobrecarga entera. Los parámetros enum se resuelven por nombre igual que en otros
        // métodos de este archivo. Se acumulan TODOS los intentos fallidos para que el mensaje
        // de error final describa el panorama completo, no solo el último candidato.
        //
        // El diagnóstico real del usuario reveló DOS sobrecargas legítimas con parámetros que
        // el emparejamiento anterior confundía:
        //  - Create(CivilDocument,String,String,String,String,String): un parámetro se llama
        //    literalmente "siteName" (nombre expuesto en el propio mensaje de excepción de
        //    Civil3D). Pasar allí el nombre del alineamiento causaba "Can not get site ID from
        //    site name" porque Civil3D buscaba un Sitio inexistente con ese nombre. La
        //    convención de Civil3D es que "" (cadena vacía) significa "sin Sitio".
        //  - Create(CivilDocument,String,ObjectId,ObjectId,ObjectId,ObjectId[,AlignmentType]):
        //    uno de los ObjectId es un "layerId" que debe ser el ObjectId REAL de la capa
        //    activa en ESTA base de datos, no ObjectId.Null — pasar Null ahí producía
        //    "Not in the same database!" (Civil3D exige un ObjectId válido y perteneciente a
        //    la misma Database, no un sentinela nulo, para el parámetro de capa).
        // Por eso este método recibe el layer en sus DOS formas (nombre y ObjectId real) y
        // el emparejamiento distingue explícitamente "site" (→ "" / ObjectId.Null: sin sitio,
        // válido en ambos casos) de "layer" (→ nombre de capa o ObjectId de capa según el tipo
        // del parámetro) antes de asumir que cualquier string/ObjectId restante es el nombre o
        // el estilo del alineamiento.
        private bool TryCrearAlignmentVacio(CivilDocument civilDoc, string name, string layerName, ObjectId layerId, ObjectId styleId, ObjectId labelSetId, out ObjectId alignId, out string error)
        {
            alignId = ObjectId.Null;
            List<string> errores = new List<string>();
            var candidatos = typeof(Alignment)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == "Create" && m.ReturnType == typeof(ObjectId))
                .Where(m => !m.GetParameters().Any(p =>
                    p.ParameterType.Name.IndexOf("PolylineOptions", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    p.ParameterType.Name.IndexOf("CorridorFeatureLine", StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();

            if (candidatos.Count == 0)
            {
                error = "No se encontró ninguna sobrecarga de Alignment.Create para un alineamiento vacío (sin PolylineOptions ni CorridorFeatureLine) en esta versión.";
                return false;
            }

            foreach (var mi in candidatos)
            {
                var ps = mi.GetParameters();
                bool descartar = false;
                object?[] args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    string pn = ps[i].Name ?? "";
                    if (pt == typeof(CivilDocument)) args[i] = civilDoc;
                    else if (pt == typeof(string) && pn.IndexOf("site", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = ""; // "" = sin Sitio (convención Civil3D)
                    else if (pt == typeof(string) && pn.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = layerName;
                    else if (pt == typeof(string) && pn.IndexOf("descri", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = "";
                    else if (pt == typeof(string)) args[i] = name;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("site", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = ObjectId.Null; // sin Sitio
                    else if (pt == typeof(ObjectId) && pn.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = layerId; // debe ser un ObjectId real de la Database actual
                    else if (pt == typeof(ObjectId) && pn.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = labelSetId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("style", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = styleId;
                    else if (pt == typeof(ObjectId)) args[i] = ObjectId.Null;
                    else if (pt.IsEnum)
                    {
                        object? val = BuscarValorDeEnumPorNombre(pt, "Centerline", "Default", "Normal", "Standard", "None");
                        if (val == null) { descartar = true; break; }
                        args[i] = val;
                    }
                    else if (!pt.IsValueType && pt != typeof(string))
                    {
                        // Clase u opción no reconocida por tipo/nombre: se intenta instanciar
                        // con su constructor sin argumentos en vez de descartar la sobrecarga.
                        try { args[i] = Activator.CreateInstance(pt); }
                        catch { descartar = true; break; }
                        if (args[i] == null) { descartar = true; break; }
                    }
                    else { descartar = true; break; }
                }
                if (descartar) { errores.Add($"Create({string.Join(",", ps.Select(p => p.ParameterType.Name))}): parámetro no reconocible."); continue; }
                try
                {
                    object? resultado = mi.Invoke(null, args);
                    if (resultado is ObjectId id && id != ObjectId.Null) { alignId = id; error = ""; return true; }
                    errores.Add($"Create({string.Join(",", ps.Select(p => p.ParameterType.Name))}): se ejecutó pero devolvió un ObjectId nulo.");
                }
                catch (System.Exception ex) { errores.Add($"Create({string.Join(",", ps.Select(p => p.ParameterType.Name))}): {ObtenerMensajeReal(ex)}"); }
            }
            error = errores.Count > 0 ? string.Join(" | ", errores) : "Ninguna sobrecarga de Alignment.Create fue compatible.";
            return false;
        }

        // Crea, por reflexión, un alineamiento de desfase (offset) para los carriles izquierdo/
        // derecho (usados para dibujar el sobreancho). Antes se llamaba a
        // Alignment.CreateOffsetAlignment(...) de forma DIRECTA con un orden de parámetros
        // supuesto; el compilador del usuario reportó "Argumento 3: no se puede convertir de
        // ObjectId a string", es decir, el orden/tipos reales de esta sobrecarga en su versión
        // de Civil3D no coinciden con lo asumido. Se resuelve igual que TryCrearAlignmentVacio:
        // se busca la sobrecarga real por reflexión y se arman los argumentos según el TIPO
        // (y el NOMBRE del parámetro, cuando hace falta desambiguar) de cada uno.
        private bool TryCrearAlignmentOffset(ObjectId parentAlignId, string name, double offset, ObjectId styleId, double startStation, double endStation, out ObjectId offsetAlignId, out string error)
        {
            offsetAlignId = ObjectId.Null; error = "";
            var candidatos = typeof(Alignment)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == "CreateOffsetAlignment" && m.ReturnType == typeof(ObjectId))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();

            if (candidatos.Count == 0)
            {
                error = "No existe Alignment.CreateOffsetAlignment en esta versión.";
                return false;
            }

            foreach (var mi in candidatos)
            {
                var ps = mi.GetParameters();
                bool descartar = false;
                object?[] args = new object?[ps.Length];
                bool primerObjectIdAsignado = false;
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    string pn = ps[i].Name ?? "";
                    if (pt == typeof(string)) args[i] = name;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("style", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = styleId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = ObjectId.Null;
                    else if (pt == typeof(ObjectId) && !primerObjectIdAsignado) { args[i] = parentAlignId; primerObjectIdAsignado = true; }
                    else if (pt == typeof(ObjectId)) args[i] = ObjectId.Null;
                    else if (pt == typeof(double) && pn.IndexOf("offset", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = offset;
                    else if (pt == typeof(double) && pn.IndexOf("start", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = startStation;
                    else if (pt == typeof(double) && (pn.IndexOf("end", StringComparison.OrdinalIgnoreCase) >= 0 || pn.IndexOf("finish", StringComparison.OrdinalIgnoreCase) >= 0)) args[i] = endStation;
                    else if (pt == typeof(double)) args[i] = offset;
                    else if (pt == typeof(bool)) args[i] = false;
                    else { descartar = true; break; }
                }
                if (descartar) { error = $"Sobrecarga CreateOffsetAlignment({string.Join(",", ps.Select(p => p.ParameterType.Name))}) tiene un parámetro no reconocible."; continue; }
                try
                {
                    object? resultado = mi.Invoke(null, args);
                    if (resultado is ObjectId id && id != ObjectId.Null) { offsetAlignId = id; return true; }
                    error = "CreateOffsetAlignment() se ejecutó pero devolvió un ObjectId nulo.";
                }
                catch (System.Exception ex) { error = ObtenerMensajeReal(ex); }
            }
            return false;
        }

        // Formatea la firma de una sobrecarga con TIPO y NOMBRE de cada parámetro (no solo el
        // tipo), para que si esta sobrecarga tampoco es compatible, el mensaje de error ya
        // traiga toda la información necesaria para ajustarla sin otra ronda de diagnóstico.
        private string FormatearFirma(string metodo, System.Reflection.ParameterInfo[] ps) =>
            $"{metodo}({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})";

        // Crea, por reflexión, el perfil del terreno natural a partir de una superficie
        // (antes: Profile.CreateFromSurface(...) llamado de forma DIRECTA con un orden de
        // parámetros supuesto). El diagnóstico real reveló TRES sobrecargas distintas y dos
        // clases de error nuevas:
        //  - "Object id of ... LayerTableRecord is expected. (Parameter 'layerId')": un
        //    parámetro ObjectId de "layer" caía en el catch-all genérico (que le asignaba
        //    alignmentId) porque este método nunca tuvo un ObjectId de capa real que ofrecer.
        //    Ahora recibe layerId explícito (debe ser un ObjectId real de la Database, p. ej.
        //    db.Clayer — igual que ya se corrigió en TryCrearAlignmentVacio).
        //  - "alignmentName cannot be blank.": una sobrecarga identifica el eje y la
        //    superficie por NOMBRE (string), no por ObjectId — parámetros "alignmentName" y
        //    "surfaceName" que antes caían en el catch-all de string y recibían "" porque el
        //    hueco de "nombre" ya lo había tomado profileName. Ahora se distinguen
        //    explícitamente ANTES del catch-all de nombre.
        private bool TryCrearPerfilDesdeSuperficie(CivilDocument civilDoc, string name, ObjectId alignmentId, string alignmentName, ObjectId surfaceId, string surfaceName, string layer, ObjectId layerId, ObjectId styleId, ObjectId labelSetId, out ObjectId profileId, out string error)
        {
            profileId = ObjectId.Null;
            List<string> errores = new List<string>();
            var candidatos = typeof(Profile)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == "CreateFromSurface" && m.ReturnType == typeof(ObjectId))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();
            if (candidatos.Count == 0) { error = "No existe Profile.CreateFromSurface en esta versión."; return false; }

            foreach (var mi in candidatos)
            {
                var ps = mi.GetParameters();
                bool descartar = false;
                bool nombreAsignado = false;
                object?[] args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    string pn = ps[i].Name ?? "";
                    if (pt == typeof(CivilDocument)) args[i] = civilDoc;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("surface", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = surfaceId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("align", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = alignmentId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = layerId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = labelSetId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("style", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = styleId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("site", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = ObjectId.Null;
                    else if (pt == typeof(ObjectId)) args[i] = alignmentId;
                    else if (pt == typeof(string) && pn.IndexOf("align", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = alignmentName;
                    else if (pt == typeof(string) && pn.IndexOf("surface", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = surfaceName;
                    else if (pt == typeof(string) && pn.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = layer;
                    else if (pt == typeof(string) && pn.IndexOf("site", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = "";
                    else if (pt == typeof(string) && pn.IndexOf("descri", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = "";
                    else if (pt == typeof(string) && !nombreAsignado) { args[i] = name; nombreAsignado = true; }
                    else if (pt == typeof(string)) args[i] = "";
                    else if (pt == typeof(double)) args[i] = 0.0;
                    else if (pt == typeof(bool)) args[i] = false;
                    else { descartar = true; break; }
                }
                if (descartar) { errores.Add(FormatearFirma("CreateFromSurface", ps) + ": parámetro no reconocible."); continue; }
                try
                {
                    object? resultado = mi.Invoke(null, args);
                    if (resultado is ObjectId id && id != ObjectId.Null) { profileId = id; error = ""; return true; }
                    errores.Add(FormatearFirma("CreateFromSurface", ps) + ": se ejecutó pero devolvió un ObjectId nulo.");
                }
                catch (System.Exception ex) { errores.Add($"{FormatearFirma("CreateFromSurface", ps)}: {ObtenerMensajeReal(ex)}"); }
            }
            error = errores.Count > 0 ? string.Join(" | ", errores) : "Ninguna sobrecarga de CreateFromSurface fue compatible.";
            return false;
        }

        // Crea, por reflexión, la rasante (perfil de diseño editable) vía Profile.CreateByLayout,
        // con la misma protección que TryCrearPerfilDesdeSuperficie (CivilDocument, layerId
        // real, alignmentName por si existe una sobrecarga análoga por nombre, strings
        // adicionales tipo site/descripción, y doubles/bool por defecto).
        private bool TryCrearPerfilPorDiseno(CivilDocument civilDoc, string name, ObjectId alignmentId, string alignmentName, string layer, ObjectId layerId, ObjectId styleId, ObjectId labelSetId, out ObjectId profileId, out string error)
        {
            profileId = ObjectId.Null;
            List<string> errores = new List<string>();
            var candidatos = typeof(Profile)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == "CreateByLayout" && m.ReturnType == typeof(ObjectId))
                .OrderBy(m => m.GetParameters().Length)
                .ToList();
            if (candidatos.Count == 0) { error = "No existe Profile.CreateByLayout en esta versión."; return false; }

            foreach (var mi in candidatos)
            {
                var ps = mi.GetParameters();
                bool descartar = false;
                bool nombreAsignado = false;
                object?[] args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    string pn = ps[i].Name ?? "";
                    if (pt == typeof(CivilDocument)) args[i] = civilDoc;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = layerId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("align", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = alignmentId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = labelSetId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("style", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = styleId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("site", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = ObjectId.Null;
                    else if (pt == typeof(ObjectId)) args[i] = alignmentId;
                    else if (pt == typeof(string) && pn.IndexOf("align", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = alignmentName;
                    else if (pt == typeof(string) && pn.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = layer;
                    else if (pt == typeof(string) && pn.IndexOf("site", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = "";
                    else if (pt == typeof(string) && pn.IndexOf("descri", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = "";
                    else if (pt == typeof(string) && !nombreAsignado) { args[i] = name; nombreAsignado = true; }
                    else if (pt == typeof(string)) args[i] = "";
                    else if (pt == typeof(double)) args[i] = 0.0;
                    else if (pt == typeof(bool)) args[i] = false;
                    else { descartar = true; break; }
                }
                if (descartar) { errores.Add(FormatearFirma("CreateByLayout", ps) + ": parámetro no reconocible."); continue; }
                try
                {
                    object? resultado = mi.Invoke(null, args);
                    if (resultado is ObjectId id && id != ObjectId.Null) { profileId = id; error = ""; return true; }
                    errores.Add(FormatearFirma("CreateByLayout", ps) + ": se ejecutó pero devolvió un ObjectId nulo.");
                }
                catch (System.Exception ex) { errores.Add($"{FormatearFirma("CreateByLayout", ps)}: {ObtenerMensajeReal(ex)}"); }
            }
            error = errores.Count > 0 ? string.Join(" | ", errores) : "Ninguna sobrecarga de CreateByLayout fue compatible.";
            return false;
        }

        // Crea, por reflexión, la vista de perfil (ProfileView.Create). En la firma real este
        // método normalmente NO devuelve el ObjectId de la vista creada (se usa solo por su
        // efecto), así que aquí el éxito se define como "se invocó sin lanzar excepción",
        // sin exigir un tipo de retorno concreto. Incluye la misma protección CivilDocument/
        // strings adicionales/doubles-bool por defecto que los métodos de Profile, por si esta
        // versión también expone una sobrecarga con esa forma ampliada.
        private bool TryCrearVistaDePerfil(CivilDocument civilDoc, ObjectId alignmentId, Point3d origin, string name, ObjectId bandSetStyleId, ObjectId styleId, out string error)
        {
            List<string> errores = new List<string>();
            var candidatos = typeof(ProfileView)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name == "Create")
                .OrderBy(m => m.GetParameters().Length)
                .ToList();
            if (candidatos.Count == 0) { error = "No existe ProfileView.Create en esta versión."; return false; }

            foreach (var mi in candidatos)
            {
                var ps = mi.GetParameters();
                bool descartar = false;
                bool nombreAsignado = false;
                object?[] args = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    string pn = ps[i].Name ?? "";
                    if (pt == typeof(CivilDocument)) args[i] = civilDoc;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("align", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = alignmentId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("band", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = bandSetStyleId;
                    else if (pt == typeof(ObjectId) && pn.IndexOf("style", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = styleId;
                    else if (pt == typeof(ObjectId)) args[i] = alignmentId;
                    else if (pt == typeof(Point3d)) args[i] = origin;
                    else if (pt == typeof(string) && pn.IndexOf("site", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = "";
                    else if (pt == typeof(string) && pn.IndexOf("descri", StringComparison.OrdinalIgnoreCase) >= 0) args[i] = "";
                    else if (pt == typeof(string) && !nombreAsignado) { args[i] = name; nombreAsignado = true; }
                    else if (pt == typeof(string)) args[i] = "";
                    else if (pt == typeof(double)) args[i] = 0.0;
                    else if (pt == typeof(bool)) args[i] = false;
                    else { descartar = true; break; }
                }
                if (descartar) { errores.Add(FormatearFirma("Create", ps) + ": parámetro no reconocible."); continue; }
                try
                {
                    mi.Invoke(null, args);
                    error = "";
                    return true;
                }
                catch (System.Exception ex) { errores.Add($"{FormatearFirma("Create", ps)}: {ObtenerMensajeReal(ex)}"); }
            }
            error = errores.Count > 0 ? string.Join(" | ", errores) : "Ninguna sobrecarga de ProfileView.Create fue compatible.";
            return false;
        }

        // Criterio del MDG INVIAS 2008 (numeral no identificado con certeza a partir de este
        // texto — cítese en la memoria de forma genérica hasta confirmar el número exacto): la
        // longitud mínima aceptable del tramo circular central de una curva horizontal debe ser
        // igual a la distancia recorrida por un vehículo a la Velocidad Específica de la curva
        // (VCH) durante 2 s. Lc_min = VCH(km/h) × 2 s ÷ 3.6 = VCH / 1.8 (metros). Por la
        // hipótesis VCH = Vtr adoptada en todo este modelo (ver memoria, numeral de alcance),
        // se evalúa con vtr directamente.
        private double ObtenerLongitudMinimaCurvaCircular(double vch) => vch / 1.8;

        // Longitud del tramo circular central de la curva: Rc × (Δ − 2·θs), donde θs = Le/(2·Rc)
        // es el ángulo (rad) barrido por cada espiral (0 si la curva no lleva espiral). Si
        // Δ ≤ 2·θs no queda tramo circular propio (curva espiral-espiral) — ese es un caso
        // geométrico distinto, así que se señala con tieneTramoCircular = false en vez de
        // reportarlo como un incumplimiento del mínimo de longitud circular.
        private double CalcularLongitudTramoCircular(double radio, double deltaRad, double le, out bool tieneTramoCircular)
        {
            double thetaS = le > 0 ? le / (2.0 * radio) : 0.0;
            double deltaCircular = deltaRad - 2.0 * thetaS;
            tieneTramoCircular = deltaCircular > 0;
            return tieneTramoCircular ? radio * deltaCircular : 0.0;
        }

        // Punto de partida ya válido (Te cabe en la entretangencia disponible) que, sin embargo,
        // puede dar un tramo circular Lc más corto que el mínimo normativo — es exactamente lo
        // que ocurre en los modos Compacto/Vía urbana, que parten deliberadamente del radio
        // mínimo normativo (el más corto posible), y también puede ocurrir en el modo Óptimo si
        // la búsqueda inicial no llegó ya al máximo que realmente cabe. Se intenta CRECER el
        // radio en pasos de 5 m — sin dejar de caber en el espacio disponible ni superar el
        // techo RADIO_MAXIMO_SIN_ESPIRAL — hasta lograr Lc ≥ Lc_min. Si ni siquiera al máximo
        // radio que cabe se alcanza el mínimo, se devuelve ese máximo (el mejor disponible) y se
        // informa el incumplimiento al llamador para que lo reporte, en vez de forzar un radio
        // que Civil3D terminaría rechazando por no caber.
        private double CrecerRadioParaLongitudMinimaCircular(double radioInicial, double deltaRad, double vtr, int catIdx, double anchoCarril, double deltaSMax, double espacioMaximoTangente, bool conEspiral, out bool cumpleLongitudMinima)
        {
            const double RADIO_MAXIMO_SIN_ESPIRAL = 5000.0;
            double lcMin = ObtenerLongitudMinimaCurvaCircular(vtr);
            double radioPrueba = radioInicial;
            double mejorRadio = radioInicial;
            cumpleLongitudMinima = false;

            while (radioPrueba <= RADIO_MAXIMO_SIN_ESPIRAL)
            {
                double le = 0.0;
                double Te;
                if (conEspiral && radioPrueba <= 1000.0)
                {
                    double eCurva = ObtenerPeraltePorRadio(vtr, radioPrueba, catIdx);
                    le = ObtenerLongitudEspiralNormativa(vtr, radioPrueba, eCurva, anchoCarril, deltaSMax);
                    double p = (le * le) / (24.0 * radioPrueba);
                    double k = le / 2.0 - (le * le * le) / (240.0 * radioPrueba * radioPrueba);
                    Te = (radioPrueba + p) * Math.Tan(deltaRad / 2.0) + k;
                }
                else
                {
                    Te = radioPrueba * Math.Tan(deltaRad / 2.0);
                }

                if (Te > espacioMaximoTangente * 0.90) break; // ya no cabe: no se puede crecer más

                mejorRadio = radioPrueba;
                double lc = CalcularLongitudTramoCircular(radioPrueba, deltaRad, le, out bool tieneTramoCircular);
                if (!tieneTramoCircular || lc >= lcMin) { cumpleLongitudMinima = true; return radioPrueba; }

                radioPrueba += 5.0;
            }
            return mejorRadio;
        }

        // Calcula el radio óptimo para el PI. ANTES partía de 3×Rmín normativo, que para
        // velocidades de diseño típicas (Rmín de 20-120 m) da un radio de arranque de apenas
        // 60-360 m — muy por debajo de 1000 m — así que la búsqueda, que solo RECORTA el
        // radio hacia abajo, casi nunca llegaba a proponer un Radio > 1000 m aunque la
        // entretangencia disponible lo permitiera de sobra: el resultado era que TODOS los PI
        // terminaban con espiral, sin importar cuánto espacio recto hubiera. Ahora se parte del
        // radio MÁS GRANDE que cabría en el espacio disponible si la curva fuera puramente
        // circular (sin espiral: Te = R·tan(Δ/2)) y se reduce desde ahí solo si hace falta —
        // así, un PI con buena entretangencia sí resulta en Radio > 1000 m (sin espiral,
        // numeral 3.7), y uno con PI muy próximos sigue reduciéndose hasta lo que quepa, nunca
        // por debajo del mínimo normativo (Tablas 3.1/3.2/3.3).
        private double CalcularRadioOptimoParaEspiral(double deltaRad, double vtr, int catIdx, double anchoCarril, double deltaSMax, double espacioMaximoTangente)
        {
            double rMinNormativo = CalcularRadioMinimo(vtr, catIdx);
            double lMinEstetica = Math.Max(30.0, 0.6 * vtr);
            // Tanto radioPorEstetica (∝ 1/deltaRad) como radioPorEspacioDisponible (∝ 1/tan(Δ/2))
            // se disparan hacia un radio absurdamente grande cuando deltaRad es muy pequeño
            // (matemáticamente correcto — un PI casi recto "cabe" con cualquier radio — pero
            // Civil3D rechaza un valor tan extremo, y la curva quedaba sin insertar, dejando el
            // PI como un quiebre recto sin aviso claro). RADIO_MAXIMO_SIN_ESPIRAL acota el
            // resultado final a un techo generoso (ya muy por encima del umbral de espiral
            // obligatoria de 1000 m, así que sigue siendo "sin espiral" a efectos del numeral
            // 3.7) que Civil3D sí acepta.
            const double RADIO_MAXIMO_SIN_ESPIRAL = 5000.0;
            double radioPorEstetica = lMinEstetica / deltaRad;
            double radioPorEspacioDisponible = (espacioMaximoTangente * 0.90) / Math.Tan(deltaRad / 2.0);
            double radioPrueba = Math.Min(RADIO_MAXIMO_SIN_ESPIRAL, Math.Max(rMinNormativo, Math.Max(radioPorEstetica, radioPorEspacioDisponible)));

            bool radioEncontrado = false;
            while (radioPrueba >= rMinNormativo && !radioEncontrado)
            {
                double Te;
                if (radioPrueba <= 1000.0)
                {
                    double eCurva = ObtenerPeraltePorRadio(vtr, radioPrueba, catIdx);
                    double Le = ObtenerLongitudEspiralNormativa(vtr, radioPrueba, eCurva, anchoCarril, deltaSMax);
                    double p = (Le * Le) / (24.0 * radioPrueba);
                    double k = Le / 2.0 - (Le * Le * Le) / (240.0 * radioPrueba * radioPrueba);
                    Te = (radioPrueba + p) * Math.Tan(deltaRad / 2.0) + k;
                }
                else
                {
                    Te = radioPrueba * Math.Tan(deltaRad / 2.0);
                }

                if (Te <= espacioMaximoTangente * 0.90) radioEncontrado = true;
                else radioPrueba -= 5.0;
            }

            if (!radioEncontrado || radioPrueba < rMinNormativo) return rMinNormativo;
            return radioPrueba;
        }

        // Para los modos Compacto (con restricción predial) y Vía urbana: a diferencia de
        // CalcularRadioOptimoParaEspiral (que arranca en un radio grande y lo REDUCE), este
        // método arranca en el radio mínimo normativo — el más compacto posible, que es
        // justamente el objetivo de estos modos — y lo AUMENTA en pasos de 5 m solo lo
        // estrictamente necesario hasta que la tangente (Te) quepa en el espacio disponible.
        // Usar el mínimo a ciegas (sin esta verificación) fue el bug real reportado: en el modo
        // Compacto varias espirales no cabían en la entretangencia disponible al mínimo radio y
        // Civil3D las rechazaba en silencio. "conEspiral" decide si Te se calcula con las
        // correcciones p/k de la espiral (Compacto, que si el radio ≤1000 m sí inserta espiral)
        // o con la fórmula circular simple (Vía urbana, que nunca inserta espiral y por tanto
        // nunca necesita ese margen adicional).
        private double CalcularRadioCompactoQueQuepa(double deltaRad, double vtr, int catIdx, double anchoCarril, double deltaSMax, double espacioMaximoTangente, bool conEspiral)
        {
            double rMinNormativo = CalcularRadioMinimo(vtr, catIdx);
            const double RADIO_MAXIMO_SIN_ESPIRAL = 5000.0;
            double radioPrueba = rMinNormativo;

            while (radioPrueba <= RADIO_MAXIMO_SIN_ESPIRAL)
            {
                double Te;
                if (conEspiral && radioPrueba <= 1000.0)
                {
                    double eCurva = ObtenerPeraltePorRadio(vtr, radioPrueba, catIdx);
                    double Le = ObtenerLongitudEspiralNormativa(vtr, radioPrueba, eCurva, anchoCarril, deltaSMax);
                    double p = (Le * Le) / (24.0 * radioPrueba);
                    double k = Le / 2.0 - (Le * Le * Le) / (240.0 * radioPrueba * radioPrueba);
                    Te = (radioPrueba + p) * Math.Tan(deltaRad / 2.0) + k;
                }
                else
                {
                    Te = radioPrueba * Math.Tan(deltaRad / 2.0);
                }

                if (Te <= espacioMaximoTangente * 0.90) return radioPrueba;
                radioPrueba += 5.0;
            }
            // No se encontró ningún radio (ni siquiera hasta el techo de 5000 m) que quepa en el
            // espacio disponible — geométricamente el PI está demasiado próximo a sus vecinos.
            // Se devuelve el mínimo de todos modos; el llamador reportará el fallo real de
            // Civil3D al intentar insertar la curva/espiral, en vez de dejar de intentarlo.
            return rMinNormativo;
        }

        // Busca, entre los nombres reales de un enum obtenido por reflexión, el primero que
        // contenga (sin distinguir mayúsculas/minúsculas) alguna de las palabras candidatas,
        // en el orden de preferencia dado. Devuelve el valor del enum ya instanciado, o null
        // si ninguna coincide.
        private object? BuscarValorDeEnumPorNombre(Type enumType, params string[] preferencias)
        {
            if (!enumType.IsEnum) return null;
            string[] nombres = Enum.GetNames(enumType);
            foreach (string pref in preferencias)
            {
                string? encontrado = nombres.FirstOrDefault(n => n.IndexOf(pref, StringComparison.OrdinalIgnoreCase) >= 0);
                if (encontrado != null) return Enum.Parse(enumType, encontrado);
            }
            return null;
        }

        // Devuelve, en orden de preferencia, los candidatos a probar para un parámetro enum:
        // primero el que coincida por nombre con alguna pista (si hay una), y DESPUÉS todos
        // los demás valores del enum — en vez de descartar la sobrecarga entera cuando ninguna
        // pista coincide. Esto corrigió un caso real: el diagnóstico reveló que SpiralCurveType
        // tiene los valores InCurve/OutCurve (nada que ver con "tipo de curva"), así que
        // ninguna de las pistas que usábamos ("Clothoid", "Longitud", etc.) podía coincidir
        // jamás — el candidato se descartaba en silencio aunque el método SÍ fuera el correcto.
        // Probar todos los valores restantes, en vez de rendirse, encuentra igual el que
        // funcione sin tener que adivinar el nombre exacto de antemano.
        private object[] ObtenerCandidatosDeEnum(Type enumType, params string[] hints)
        {
            object? porHint = BuscarValorDeEnumPorNombre(enumType, hints);
            var todos = Enum.GetValues(enumType).Cast<object>().ToList();
            if (porHint == null) return todos.ToArray();
            var resto = todos.Where(v => !Equals(v, porHint));
            return new[] { porHint }.Concat(resto).ToArray();
        }

        // MethodInfo.Invoke() envuelve CUALQUIER excepción lanzada por el método real dentro
        // de un TargetInvocationException genérico ("Exception has been thrown by the target
        // of an invocation"), y el motivo real queda oculto en su InnerException. Todos los
        // catch de este archivo que siguen a un mi.Invoke(...) deben leer el mensaje a través
        // de este helper — leer ex.Message directamente sobre el resultado de Invoke() nunca
        // muestra la causa real del fallo en Civil3D.
        private string ObtenerMensajeReal(System.Exception ex) =>
            ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null
                ? tie.InnerException.Message
                : ex.Message;

        // Las dos firmas de TryInsertarEspiralAutomatica son candidatos razonables tomados
        // de la documentación pública de Civil3D, pero no se pudieron verificar contra el
        // ensamblado real (no disponible en este entorno de desarrollo). Si ambas fallan,
        // en vez de seguir adivinando nombres, este método usa reflexión para listar TODOS
        // los métodos públicos de "Alignment" y "Alignment.Entities" que contengan la
        // palabra "Spiral" en el ensamblado REAL que está cargado en la sesión de Civil3D
        // del usuario — con su firma completa (tipos y nombres de parámetros) — y los
        // imprime en la ventana de comandos. Con ese listado real se puede escribir la
        // llamada correcta con certeza, en vez de por ensayo y error.
        private void DiagnosticarMetodosDeEspiral(Editor ed, Alignment alignment)
        {
            try
            {
                ed.WriteMessage("\n[INVIAS] ====== DIAGNÓSTICO: métodos disponibles para crear espirales en esta versión de Civil3D ======");
                object entidadesObj = alignment.Entities;
                var candidatos = new (string etiqueta, Type tipo)[] {
                    ("Alignment", alignment.GetType()),
                    ("Alignment.Entities", entidadesObj.GetType())
                };
                HashSet<Type> enumsVistos = new HashSet<Type>();
                foreach (var (etiqueta, tipo) in candidatos)
                {
                    ed.WriteMessage($"\n[INVIAS] Tipo real de {etiqueta}: {tipo.FullName}");
                    var metodos = tipo.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Where(m => m.Name.IndexOf("Spiral", StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(m => m.Name)
                        .ToList();
                    if (metodos.Count == 0)
                    {
                        ed.WriteMessage($"\n[INVIAS]   (sin métodos con \"Spiral\" en el nombre)");
                        continue;
                    }
                    foreach (var m in metodos)
                    {
                        string parametros = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        ed.WriteMessage($"\n[INVIAS]   {m.ReturnType.Name} {m.Name}({parametros})");
                        foreach (var p in m.GetParameters())
                            if (p.ParameterType.IsEnum) enumsVistos.Add(p.ParameterType);
                    }
                }
                // Los nombres de método por sí solos no bastan para saber qué VALOR de cada
                // enum corresponde a "espiral tipo clotoide", "parámetro = longitud", etc. — se
                // listan los valores reales de cada enum usado, para no seguir adivinando por
                // coincidencia parcial de texto (BuscarValorDeEnumPorNombre) sino con certeza.
                if (enumsVistos.Count > 0)
                {
                    ed.WriteMessage("\n[INVIAS] ------ Valores reales de los enums usados por esos métodos ------");
                    foreach (var enumType in enumsVistos.OrderBy(t => t.Name))
                        ed.WriteMessage($"\n[INVIAS]   {enumType.Name}: {string.Join(", ", Enum.GetNames(enumType))}");
                }
                ed.WriteMessage("\n[INVIAS] ====== FIN DEL DIAGNÓSTICO — copie este bloque completo ======\n");
            }
            catch (System.Exception exDiag)
            {
                ed.WriteMessage($"\n[INVIAS] No se pudo completar el diagnóstico de métodos de espiral: {exDiag.Message}");
            }
        }

        // Igual que DiagnosticarMetodosDeEspiral, pero para la inserción de curvas circulares
        // sin espiral (Radio > 1000 m, o modo Vía urbana). Antes este fallo nunca se
        // diagnosticaba — TryInsertarCurvaLibre ya reporta un detalle enriquecido (todas las
        // sobrecargas probadas, con nombre y tipo de cada parámetro), pero sin esta lista
        // completa de métodos candidatos y sus enums reales, no hay forma de saber con certeza
        // si el nombre real difiere de "AddFreeCurveBetweenTangents" o si el problema es otro.
        private void DiagnosticarMetodosDeCurvaLibre(Editor ed, Alignment alignment)
        {
            try
            {
                ed.WriteMessage("\n[INVIAS] ====== DIAGNÓSTICO: métodos disponibles para crear curvas circulares libres en esta versión de Civil3D ======");
                object entidadesObj = alignment.Entities;
                var candidatos = new (string etiqueta, Type tipo)[] {
                    ("Alignment", alignment.GetType()),
                    ("Alignment.Entities", entidadesObj.GetType())
                };
                HashSet<Type> enumsVistos = new HashSet<Type>();
                foreach (var (etiqueta, tipo) in candidatos)
                {
                    ed.WriteMessage($"\n[INVIAS] Tipo real de {etiqueta}: {tipo.FullName}");
                    var metodos = tipo.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                        .Where(m => m.Name.IndexOf("Curve", StringComparison.OrdinalIgnoreCase) >= 0)
                        .OrderBy(m => m.Name)
                        .ToList();
                    if (metodos.Count == 0)
                    {
                        ed.WriteMessage($"\n[INVIAS]   (sin métodos con \"Curve\" en el nombre)");
                        continue;
                    }
                    foreach (var m in metodos)
                    {
                        string parametros = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                        ed.WriteMessage($"\n[INVIAS]   {m.ReturnType.Name} {m.Name}({parametros})");
                        foreach (var p in m.GetParameters())
                            if (p.ParameterType.IsEnum) enumsVistos.Add(p.ParameterType);
                    }
                }
                if (enumsVistos.Count > 0)
                {
                    ed.WriteMessage("\n[INVIAS] ------ Valores reales de los enums usados por esos métodos ------");
                    foreach (var enumType in enumsVistos.OrderBy(t => t.Name))
                        ed.WriteMessage($"\n[INVIAS]   {enumType.Name}: {string.Join(", ", Enum.GetNames(enumType))}");
                }
                ed.WriteMessage("\n[INVIAS] ====== FIN DEL DIAGNÓSTICO — copie este bloque completo ======\n");
            }
            catch (System.Exception exDiag)
            {
                ed.WriteMessage($"\n[INVIAS] No se pudo completar el diagnóstico de métodos de curva libre: {exDiag.Message}");
            }
        }

        // Tabla 4.3 INVIAS - Longitud mínima de la tangente vertical (VTV = VTR asumida)
        private double ObtenerLongitudMinimaTangenteVertical(double vtr)
        {
            int v = (int)Math.Round(vtr);
            if (v <= 20) return 40.0;
            if (v <= 30) return 60.0;
            if (v <= 40) return 80.0;
            if (v <= 50) return 140.0;
            if (v <= 60) return 170.0;
            if (v <= 70) return 195.0;
            if (v <= 80) return 225.0;
            if (v <= 90) return 250.0;
            if (v <= 100) return 280.0;
            if (v <= 110) return 305.0;
            if (v <= 120) return 335.0;
            return 360.0; // 130 km/h
        }

        // Tabla 4.4 INVIAS - Valor de Kmín (redondeado) para curvas verticales convexas/cóncavas
        private double ObtenerKMinimo(double vtr, bool convexa)
        {
            int v = (int)Math.Round(vtr);
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
            double[] kConvexa = { 1, 2, 4, 7, 11, 17, 26, 39, 52, 74, 95, 124 };
            double[] kConcava = { 3, 6, 9, 13, 18, 23, 30, 38, 45, 55, 63, 73 };
            double[] k = convexa ? kConvexa : kConcava;
            for (int i = 0; i < vs.Length; i++) if (v <= vs[i]) return k[i];
            return k[k.Length - 1];
        }

        // Tabla 4.4 INVIAS - Distancia de visibilidad de parada (DP) asociada a la VCV
        private double ObtenerDistanciaVisibilidadParada(double vtr)
        {
            int v = (int)Math.Round(vtr);
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
            double[] dp = { 20, 35, 50, 65, 85, 105, 130, 160, 185, 220, 250, 285 };
            for (int i = 0; i < vs.Length; i++) if (v <= vs[i]) return dp[i];
            return dp[dp.Length - 1];
        }

        // Tabla 4.4 INVIAS - Longitud mínima según criterio de operación (nota 1: piso de 20 m)
        private double ObtenerLongitudMinimaCurvaVertical(double vtr) => Math.Max(20.0, 0.6 * vtr);

        private void CmbNormativa_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCategoriaVia == null || CmbTipoTerreno == null || CmbVtr == null) return;
            int catIdx = CmbCategoriaVia.SelectedIndex;
            int terIdx = CmbTipoTerreno.SelectedIndex;
            int targetVtr = 30;

            if (catIdx == 0) {
                if (terIdx == 0) targetVtr = 110; else if (terIdx == 1) targetVtr = 100; else if (terIdx == 2) targetVtr = 80; else targetVtr = 70;
            } else if (catIdx == 1) {
                if (terIdx == 0) targetVtr = 90; else if (terIdx == 1) targetVtr = 80; else if (terIdx == 2) targetVtr = 70; else targetVtr = 60;
            } else if (catIdx == 2) {
                if (terIdx == 0) targetVtr = 80; else if (terIdx == 1) targetVtr = 70; else if (terIdx == 2) targetVtr = 60; else targetVtr = 40;
            } else {
                if (terIdx == 0) targetVtr = 50; else if (terIdx == 1) targetVtr = 40; else if (terIdx == 2) targetVtr = 30; else targetVtr = 20;
            }

            foreach (ComboBoxItem item in CmbVtr.Items) {
                if (item.Content != null && item.Content.ToString() == targetVtr.ToString()) { CmbVtr.SelectedItem = item; break; }
            }
        }

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
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId surfId in surfaceIds)
                    {
                        Autodesk.Civil.DatabaseServices.Surface? surf = tr.GetObject(surfId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                        if (surf != null && surf.Name != null) CmbSuperficies.Items.Add(new ComboBoxItem { Content = surf.Name, Tag = surfId });
                    }
                    tr.Commit();
                }
            }
            if (CmbSuperficies.Items.Count > 0) CmbSuperficies.SelectedIndex = 0;
        }

        // ==========================================
        // 🔹 PESTAÑA 1: PLANTA CON FÓRMULA INVIAS ESTRICTA
        // ==========================================
        private void BtnSelectPolyline_Click(object sender, RoutedEventArgs e)
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            PromptEntityOptions peo = new PromptEntityOptions("\n[INVIAS] Seleccione la polilínea 2D del eje de la vía: ");
            PromptEntityResult per = doc.Editor.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return;

            // No se restringe la selección con AddAllowedClass: un objeto que el panel de
            // Propiedades muestra como "Polilínea" puede, en tiempo de ejecución, no ser la
            // clase exacta Autodesk.AutoCAD.DatabaseServices.Polyline (por ejemplo, si viene de
            // otra aplicación o de un flujo de Civil3D). En vez de rechazar con un mensaje
            // genérico que no permite diagnosticar, se lee el objeto y, si no es del tipo
            // esperado, se reporta su tipo REAL para saber con certeza qué es, en vez de adivinar.
            //
            // Se aceptan las TRES variantes de polilínea de AutoCAD, no solo la LWPOLYLINE
            // moderna: Polyline2d y Polyline3d (los formatos "antiguos", frecuentes en dibujos
            // heredados o importados) también tienen vértices reales con los que se puede
            // construir la lista de PI.
            //
            // También se acepta una Spline dibujada por VÉRTICES DE CONTROL: se usan esos
            // vértices de control directamente como PI (líneas rectas entre ellos), a petición
            // explícita del usuario, sabiendo que NO son geométricamente idénticos a la curva
            // visible (en una B-spline cúbica, los vértices de control "jalan" la curva pero no
            // están sobre ella) — por eso el aviso de confirmación lo advierte, en vez de dar a
            // entender que el eje calcado será una réplica exacta de la Spline.
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                Autodesk.AutoCAD.DatabaseServices.DBObject obj = tr.GetObject(per.ObjectId, OpenMode.ForRead);
                tr.Commit();

                if (obj is Polyline || obj is Polyline2d || obj is Polyline3d)
                {
                    SelectedPolylineId = per.ObjectId;
                    MessageBox.Show("Polilínea del eje seleccionada con éxito.", "Selección", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (obj is Autodesk.AutoCAD.DatabaseServices.Spline)
                {
                    SelectedPolylineId = per.ObjectId;
                    MessageBox.Show("Spline seleccionada.\n\nSe usarán sus vértices de control como PI del eje (líneas rectas entre ellos). Como los vértices de control no están exactamente sobre la curva visible, el eje resultante se desviará algo de la Spline original — revíselo antes de continuar.", "Selección", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    string tipoReal = obj.GetType().FullName ?? obj.GetType().Name;
                    MessageBox.Show($"El objeto seleccionado no es una Polilínea (LWPOLYLINE, Polyline2d ni Polyline3d) ni una Spline por vértices de control.\n\nTipo real detectado: {tipoReal}\n\nComparta este tipo exacto para que la herramienta se ajuste a él.", "Selección no válida", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        // Extrae los vértices del eje como Point2d sin importar cuál de las tres variantes de
        // polilínea de AutoCAD se seleccionó. Para Polyline2d/Polyline3d los vértices son
        // entidades propias (Vertex2d / PolylineVertex3d) referenciadas por ObjectId, y solo
        // los "vértice estándar" (no los auxiliares de ajuste de spline/curva) representan
        // puntos reales de tangente recta — los de tipo CurveFit/SplineFit/SplineControl se
        // excluyen porque no son PI de diseño, son puntos de un suavizado ya aplicado en AutoCAD.
        //
        // La forma de enumerar los vértices de Polyline2d/Polyline3d (¿propiedad ".Vertices"?
        // ¿el propio objeto es IEnumerable?) resultó no ser la asumida en un primer intento
        // (el compilador del usuario reportó "Polyline2d no contiene una definición para
        // Vertices"), así que se prueba por reflexión en vez de asumir un único nombre: primero
        // si el propio objeto es enumerable (patrón habitual en AutoCAD .NET para colecciones
        // de sub-entidades, p. ej. BlockTableRecord), y si no, se busca cualquier propiedad
        // pública cuyo nombre contenga "Vert" y sea enumerable.
        private List<ObjectId> ObtenerIdsDeVertices(object polilineaAntiguoEstilo)
        {
            var lista = new List<ObjectId>();
            if (polilineaAntiguoEstilo is System.Collections.IEnumerable enumerableDirecto)
            {
                foreach (object item in enumerableDirecto) if (item is ObjectId oid) lista.Add(oid);
                if (lista.Count > 0) return lista;
            }

            // Se busca tanto entre propiedades como entre campos públicos: en este ensamblado
            // no se puede asumir cuál de las dos formas usa la API real (ya ocurrió con
            // VertexType, expuesto como campo y no como propiedad en algunos tipos).
            Type tPoli = polilineaAntiguoEstilo.GetType();
            var propVert = tPoli
                .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(p => p.Name.IndexOf("Vert", StringComparison.OrdinalIgnoreCase) >= 0
                    && typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType));
            if (propVert != null && propVert.GetValue(polilineaAntiguoEstilo) is System.Collections.IEnumerable enumerablePropiedad)
            {
                foreach (object item in enumerablePropiedad) if (item is ObjectId oid) lista.Add(oid);
                if (lista.Count > 0) return lista;
            }

            var campoVert = tPoli
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(f => f.Name.IndexOf("Vert", StringComparison.OrdinalIgnoreCase) >= 0
                    && typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType));
            if (campoVert != null && campoVert.GetValue(polilineaAntiguoEstilo) is System.Collections.IEnumerable enumerableCampo)
            {
                foreach (object item in enumerableCampo) if (item is ObjectId oid) lista.Add(oid);
            }
            return lista;
        }

        // Igual que con la enumeración de vértices, el nombre exacto del valor "estándar" del
        // enum VertexType resultó no ser el asumido (el compilador reportó que Vertex2dType no
        // tiene un miembro "Standard2dVertex"). En vez de adivinar otro nombre, se lee el valor
        // por reflexión y se acepta el vértice salvo que su nombre indique explícitamente que es
        // un punto auxiliar de ajuste de curva/spline (Spline.../CurveFit...) — así no hace
        // falta acertar el nombre exacto del miembro "normal" del enum, solo reconocer los que
        // claramente NO lo son.
        private bool EsVerticeDeTangenteRecta(object vertice)
        {
            // VertexType puede estar expuesto como propiedad o como campo público según el
            // ensamblado real; se comprueban ambos en vez de asumir uno solo.
            Type t = vertice.GetType();
            object? valor = t.GetProperty("VertexType")?.GetValue(vertice)
                ?? t.GetField("VertexType")?.GetValue(vertice);
            if (valor == null) return true;
            string nombre = valor.ToString() ?? "";
            if (nombre.IndexOf("Spline", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (nombre.IndexOf("CurveFit", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        // Extrae los vértices de control de una Spline por reflexión, en vez de asumir los
        // nombres reales de "NumControlPoints"/"GetControlPointAt" (dos intentos anteriores de
        // adivinar nombres de la API de AutoCAD .NET en esta misma sesión — Polyline2d.Vertices,
        // Vertex2dType.Standard2dVertex — resultaron incorrectos, así que aquí no se arriesga un
        // tercero): se busca cualquier propiedad pública de tipo entero cuyo nombre contenga
        // "ControlPoint" para el conteo, y cualquier método público que tome un único int y
        // devuelva Point3d cuyo nombre también contenga "ControlPoint" para leer cada vértice.
        private List<Point2d> ObtenerVerticesDeControlDeSpline(object spline)
        {
            var vertices = new List<Point2d>();
            Type t = spline.GetType();

            var propCount = t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(p => p.Name.IndexOf("ControlPoint", StringComparison.OrdinalIgnoreCase) >= 0 && p.PropertyType == typeof(int));
            if (propCount == null) return vertices;
            int n = (int)(propCount.GetValue(spline) ?? 0);
            if (n <= 0) return vertices;

            var metodoGet = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .FirstOrDefault(m => m.Name.IndexOf("ControlPoint", StringComparison.OrdinalIgnoreCase) >= 0
                    && m.ReturnType == typeof(Point3d)
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(int));
            if (metodoGet == null) return vertices;

            for (int i = 0; i < n; i++)
            {
                if (metodoGet.Invoke(spline, new object[] { i }) is Point3d p)
                    vertices.Add(new Point2d(p.X, p.Y));
            }
            return vertices;
        }

        private List<Point2d> ObtenerVerticesDeEje(Transaction tr, ObjectId id, out string error)
        {
            error = "";
            var vertices = new List<Point2d>();
            Autodesk.AutoCAD.DatabaseServices.DBObject obj = tr.GetObject(id, OpenMode.ForRead);

            if (obj is Polyline pline)
            {
                for (int i = 0; i < pline.NumberOfVertices; i++) vertices.Add(pline.GetPoint2dAt(i));
            }
            else if (obj is Autodesk.AutoCAD.DatabaseServices.Spline spline)
            {
                vertices.AddRange(ObtenerVerticesDeControlDeSpline(spline));
                if (vertices.Count < 2)
                    error = "No se pudieron leer los vértices de control de esta Spline en esta versión de AutoCAD.";
            }
            else if (obj is Polyline2d pline2d)
            {
                foreach (ObjectId vId in ObtenerIdsDeVertices(pline2d))
                {
                    Vertex2d? v = tr.GetObject(vId, OpenMode.ForRead) as Vertex2d;
                    if (v != null && EsVerticeDeTangenteRecta(v))
                        vertices.Add(new Point2d(v.Position.X, v.Position.Y));
                }
                if (vertices.Count < 2)
                    error = "Esta Polyline2d es de tipo ajuste de curva/spline (sus vértices no son puntos de tangente recta). Conviértala a una polilínea de segmentos rectos antes de seleccionarla.";
            }
            else if (obj is Polyline3d pline3d)
            {
                foreach (ObjectId vId in ObtenerIdsDeVertices(pline3d))
                {
                    PolylineVertex3d? v = tr.GetObject(vId, OpenMode.ForRead) as PolylineVertex3d;
                    if (v != null && EsVerticeDeTangenteRecta(v))
                        vertices.Add(new Point2d(v.Position.X, v.Position.Y));
                }
                if (vertices.Count < 2)
                    error = "Esta Polyline3d es de tipo ajuste de curva (sus vértices no son puntos de tangente recta). Conviértala a una polilínea de segmentos rectos antes de seleccionarla.";
            }
            else
            {
                error = $"Tipo no soportado: {obj.GetType().FullName ?? obj.GetType().Name}.";
            }

            return vertices;
        }

        // Reconstruye el eje PI por PI en vez de "calcarlo" de un tirón con
        // Alignment.Create(PolylineOptions): se crea primero cada tangente recta con
        // AddFixedLine (que devuelve un EntityId real y estable), y luego, en cada PI, se
        // decide el radio (con margen de entretangencia) y se inserta la curva/espiral por su
        // EntityId — no por posición en la colección. Esto es lo que permite que el numeral 3.7
        // (espiral obligatoria si Radio ≤ 1000 m) se aplique de verdad en la creación, no como
        // un remiendo posterior sobre arcos que Civil3D ya generó a su manera.
        private void BtnProcesarPlanta_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedPolylineId == ObjectId.Null) return;
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database; Editor ed = doc.Editor; CivilDocument civilDoc = CivilApplication.ActiveDocument;

            try
            {
                using (DocumentLock docLock = doc.LockDocument())
                {
                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        List<Point2d> vertices = ObtenerVerticesDeEje(tr, SelectedPolylineId, out string errorVertices);
                        if (vertices.Count < 2)
                        {
                            MessageBox.Show(string.IsNullOrEmpty(errorVertices)
                                ? "La polilínea del eje debe tener al menos dos vértices."
                                : $"No se pudieron obtener los vértices del eje.\n\n{errorVertices}",
                                "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        double vtr = ObtenerVelocidadDiseno();
                        int catIdx = CmbCategoriaVia.SelectedIndex;
                        double anchoCarril = ObtenerAnchoCarril();
                        double deltaSMax = ObtenerDeltaSMaximo(vtr);
                        double minRadius = CalcularRadioMinimo(vtr, catIdx);

                        // Modo de diseño en planta (CmbModoDiseno):
                        //  0 = Óptimo: el radio más grande que la entretangencia permita (el
                        //      comportamiento de siempre); espiral solo si R≤1000 m (numeral 3.7).
                        //  1 = Compacto (con restricción predial): SIEMPRE el radio mínimo
                        //      normativo (la curva más cerrada posible, menor huella), pero la
                        //      espiral se sigue insertando cuando R≤1000 m — como el mínimo casi
                        //      siempre cae en ese rango, esto en la práctica añade espiral a casi
                        //      toda curva, pero sigue siendo 100% conforme al numeral 3.7.
                        //  2 = Vía urbana: el mismo radio mínimo del modo Compacto, pero SIN
                        //      espiral en ningún caso, aunque R≤1000 m — una relajación deliberada
                        //      para contexto urbano que el Manual (pensado para carreteras) no
                        //      contempla; este modo NO garantiza cumplimiento 100% del Manual y
                        //      así se advierte al usuario en el resumen final.
                        int modoDiseno = CmbModoDiseno?.SelectedIndex ?? 0;

                        ObjectId styleId = civilDoc.Styles.AlignmentStyles.Count > 0 ? civilDoc.Styles.AlignmentStyles[0] : ObjectId.Null;
                        ObjectId labelSetId = civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles.Count > 0 ? civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles[0] : ObjectId.Null;
                        string alignmentName = "Eje_INVIAS_" + DateTime.Now.ToString("HHmmss");

                        if (!TryCrearAlignmentVacio(civilDoc, alignmentName, ObtenerNombreCapaActual(tr, db), db.Clayer, styleId, labelSetId, out ObjectId alignId, out string errorCrear))
                        {
                            MessageBox.Show($"No se pudo crear un alineamiento vacío en esta versión de Civil3D para construirlo PI por PI.\n\nDetalle: {errorCrear}\n\nComparta este detalle exacto para ajustar la herramienta a su instalación.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                        Alignment? alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment;
                        if (alignment == null)
                        {
                            MessageBox.Show("El alineamiento se creó pero no pudo abrirse para escritura.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }

                        // Paso A: una tangente fija por cada segmento recto de la polilínea original.
                        int[] lineIds = new int[vertices.Count - 1];
                        for (int i = 0; i < vertices.Count - 1; i++)
                        {
                            if (!TryAgregarLineaFija(alignment.Entities, vertices[i], vertices[i + 1], out int idLinea, out string errorLinea))
                            {
                                MessageBox.Show($"No se pudo crear la tangente recta entre los vértices {i} y {i + 1} de la polilínea.\n\nDetalle: {errorLinea}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                tr.Commit();
                                return;
                            }
                            lineIds[i] = idLinea;
                        }

                        int nEspiralesInsertadas = 0;
                        int nCurvasCircularesInsertadas = 0;
                        List<string> pendientesEspiral = new List<string>();
                        List<string> pendientesLongitudCircular = new List<string>();
                        bool diagnosticoImpreso = false;
                        bool diagnosticoCurvaImpreso = false;
                        int idxCurva = 1;

                        // Paso B/C: por cada PI interior se calcula la deflexión real, el radio
                        // óptimo (considerando la entretangencia disponible) y, según el numeral
                        // 3.7 (Radio ≤ 1000 m ⇒ espiral obligatoria), se inserta curva o espiral
                        // entre las dos tangentes vecinas usando sus EntityId reales.
                        for (int i = 1; i < vertices.Count - 1; i++)
                        {
                            Point2d pAnt = vertices[i - 1], pPi = vertices[i], pSig = vertices[i + 1];
                            double az1 = Math.Atan2(pPi.Y - pAnt.Y, pPi.X - pAnt.X);
                            double az2 = Math.Atan2(pSig.Y - pPi.Y, pSig.X - pPi.X);
                            double deltaRad = az2 - az1;
                            while (deltaRad > Math.PI) deltaRad -= 2.0 * Math.PI;
                            while (deltaRad < -Math.PI) deltaRad += 2.0 * Math.PI;
                            double deltaAbsRad = Math.Abs(deltaRad);
                            // Antes se omitía cualquier PI con deltaAbsRad < 0.0005 rad (~0.03°),
                            // pero antes de la corrección del radio máximo (arriba) ESE era
                            // precisamente el rango de deflexión donde el radio propuesto se
                            // disparaba a un valor absurdo y Civil3D rechazaba la curva — dejando
                            // el PI como un quiebre recto sin aviso. Con el radio ya acotado a
                            // 5000 m, se puede bajar este umbral a un valor que solo descarta
                            // ruido de coma flotante / vértices duplicados, no deflexiones reales
                            // aunque sean muy suaves, para que también reciban su curva.
                            if (deltaAbsRad < 0.00005) continue; // PI realmente alineado (vértices duplicados / ruido numérico)

                            double dist1 = pAnt.GetDistanceTo(pPi);
                            double dist2 = pPi.GetDistanceTo(pSig);
                            double espacioMaximoTangente = Math.Min(dist1, dist2) / 2.0;

                            double radioOptimo = modoDiseno == 0
                                ? CalcularRadioOptimoParaEspiral(deltaAbsRad, vtr, catIdx, anchoCarril, deltaSMax, espacioMaximoTangente)
                                : CalcularRadioCompactoQueQuepa(deltaAbsRad, vtr, catIdx, anchoCarril, deltaSMax, espacioMaximoTangente, conEspiral: modoDiseno == 1);
                            // Compacto (1): el radio más pequeño que normativa Y entretangencia permitan
                            // (con espiral si ≤1000 m). Vía urbana (2): igual, pero medido sin margen de
                            // espiral, porque este modo nunca la inserta.
                            string nombreCurva = $"C-{idxCurva++}";
                            bool isGreaterThan180 = false; // ángulo entre dos tangentes de una polilínea simple: siempre < 180°

                            // Criterio de longitud mínima del tramo circular (VCH × 2 s), aplicable a
                            // los 3 modos por igual: si el radio recién calculado deja un tramo
                            // circular más corto que el mínimo, se intenta CRECER el radio (dentro de
                            // la entretangencia disponible) hasta cumplirlo — en el modo Óptimo casi
                            // siempre no hay margen adicional (ya se partió del máximo que cabe), pero
                            // en Compacto/Vía urbana, que arrancan del radio mínimo normativo, es
                            // frecuente que sí haya margen para crecer sin dejar de ser la curva más
                            // cerrada que el manual y el espacio disponible permiten.
                            radioOptimo = CrecerRadioParaLongitudMinimaCircular(radioOptimo, deltaAbsRad, vtr, catIdx, anchoCarril, deltaSMax, espacioMaximoTangente, conEspiral: modoDiseno != 2, out bool cumpleLongitudMinimaCircular);
                            if (!cumpleLongitudMinimaCircular)
                            {
                                double lcMinTexto = ObtenerLongitudMinimaCurvaCircular(vtr);
                                string avisoLc = $"{nombreCurva}: con Radio {radioOptimo:F2} m (el máximo que cabe en la entretangencia disponible) el tramo circular no alcanza la longitud mínima de {lcMinTexto:F1} m (criterio MDG INVIAS 2008: distancia recorrida a VCH en 2 s). Revise el trazado (PI muy próximos) o acepte esta curva como caso extremo justificado.";
                                pendientesLongitudCircular.Add(avisoLc);
                                ed.WriteMessage($"\n[INVIAS] Aviso: {avisoLc}");
                            }

                            // Vía urbana (modo 2): nunca inserta espiral, aunque R≤1000 m — relajación
                            // deliberada fuera del numeral 3.7, advertida al usuario en el resumen final.
                            bool omitirEspiralPorModoUrbano = modoDiseno == 2;

                            if (radioOptimo > 1000.0 || omitirEspiralPorModoUrbano)
                            {
                                if (TryInsertarCurvaLibre(alignment.Entities, lineIds[i - 1], lineIds[i], radioOptimo, isGreaterThan180, out string errorCurva))
                                {
                                    nCurvasCircularesInsertadas++;
                                }
                                else
                                {
                                    string motivoSinEspiral = omitirEspiralPorModoUrbano
                                        ? "modo Vía urbana: espiral omitida deliberadamente"
                                        : "no requiere espiral según numeral 3.7 (Radio > 1000 m)";
                                    pendientesEspiral.Add($"{nombreCurva}: no se pudo insertar la curva circular (Radio {radioOptimo:F2} m, {motivoSinEspiral}). Detalle: {errorCurva}");
                                    // Antes este fallo se guardaba SOLO en el resumen final (un simple
                                    // conteo), sin mostrar nunca el detalle real ni diagnosticar por qué
                                    // — a diferencia del fallo de espiral, que sí se imprime y dispara el
                                    // diagnóstico de métodos reales. Se iguala el tratamiento aquí.
                                    ed.WriteMessage($"\n[INVIAS] Aviso: {nombreCurva} necesita curva circular (Radio {radioOptimo:F2} m) pero no se pudo insertar automáticamente en esta versión de Civil3D. Detalle: {errorCurva}");
                                    if (!diagnosticoCurvaImpreso)
                                    {
                                        DiagnosticarMetodosDeCurvaLibre(ed, alignment);
                                        diagnosticoCurvaImpreso = true;
                                    }
                                }
                                continue;
                            }

                            double eCurva = ObtenerPeraltePorRadio(vtr, radioOptimo, catIdx);
                            double le = ObtenerLongitudEspiralNormativa(vtr, radioOptimo, eCurva, anchoCarril, deltaSMax);

                            bool ok = TryInsertarEspiralAutomatica(alignment.Entities, lineIds[i - 1], lineIds[i], radioOptimo, le, isGreaterThan180, out string ultimoError);
                            if (ok)
                            {
                                nEspiralesInsertadas++;
                                ed.WriteMessage($"\n[INVIAS] {nombreCurva}: espiral insertada automáticamente (Le entrada = Le salida = {le:F2} m, Radio = {radioOptimo:F2} m).");
                            }
                            else
                            {
                                double aMinReq = Math.Sqrt(le * radioOptimo);
                                pendientesEspiral.Add($"{nombreCurva}: Radio {radioOptimo:F2} m ≤ 1000 m, requiere espiral (numeral 3.7). Insértela manualmente en Civil3D (Alignment Layout Tools) con Le entrada = Le salida = {le:F2} m (A mínimo = {aMinReq:F2} m). Motivo del fallo automático: {ultimoError}");
                                ed.WriteMessage($"\n[INVIAS] Aviso: {nombreCurva} necesita espiral (Le = {le:F2} m) pero no se pudo insertar automáticamente en esta versión de Civil3D. Detalle: {ultimoError}");
                                if (!diagnosticoImpreso)
                                {
                                    DiagnosticarMetodosDeEspiral(ed, alignment);
                                    diagnosticoImpreso = true;
                                }
                            }
                        }

                        tr.Commit();

                        string resumenEspirales = pendientesEspiral.Count == 0
                            ? (nEspiralesInsertadas > 0 ? $"\n• Espirales insertadas automáticamente (numeral 3.7): {nEspiralesInsertadas}" : "\n• Ninguna curva requiere espiral (todas con Radio > 1000 m, numeral 3.7)")
                            : $"\n• Espirales insertadas automáticamente: {nEspiralesInsertadas}\n• Curvas que AÚN requieren espiral manual: {pendientesEspiral.Count} (detalle en la ventana de comandos de AutoCAD)";
                        string resumenCirculares = nCurvasCircularesInsertadas > 0
                            ? (modoDiseno == 2
                                ? $"\n• Curvas circulares SIN espiral insertadas (modo Vía urbana): {nCurvasCircularesInsertadas}"
                                : $"\n• Curvas circulares sin espiral insertadas (Radio > 1000 m): {nCurvasCircularesInsertadas}")
                            : "";
                        string modoTexto = modoDiseno == 0 ? "Óptimo" : modoDiseno == 1 ? "Compacto (con restricción predial)" : "Vía urbana";
                        string avisoModoUrbano = modoDiseno == 2
                            ? "\n\n⚠ Modo Vía urbana: las curvas se insertaron SIN espiral de transición aunque su Radio sea ≤ 1000 m. Este modo NO garantiza el cumplimiento 100% del numeral 3.7 del Manual — úselo solo cuando el contexto urbano lo justifique."
                            : "";
                        string resumenLongitudCircular = pendientesLongitudCircular.Count == 0
                            ? "\n• Todas las curvas cumplen la longitud mínima del tramo circular (VCH × 2 s, MDG INVIAS 2008)."
                            : $"\n• Curvas con tramo circular MÁS CORTO que el mínimo (VCH × 2 s): {pendientesLongitudCircular.Count} (detalle en la ventana de comandos de AutoCAD; se creció el Radio hasta el máximo que cabe en la entretangencia disponible, sin ser suficiente).";

                        MessageBox.Show($"Alineamiento creado PI por PI.\n\n• Modo de diseño: {modoTexto}\n• Vtr: {vtr} km/h\n• Radio mínimo normativo: {minRadius:F2} m{resumenCirculares}{resumenEspirales}{resumenLongitudCircular}{avisoModoUrbano}", "Planta completada", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"Ocurrió un error procesando la planta:\n\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ==========================================
        // 🔹 PESTAÑA 2: RASANTE CON LÍMITES
        // ==========================================
        private double ObtenerPendienteMaximaINVIAS(int catIdx, double vtr) {
            int v = (int)vtr;
            switch (catIdx) {
                case 0: if (v >= 120) return 4.0; if (v >= 100) return 5.0; return 6.0;
                case 1: if (v >= 100) return 5.0; if (v >= 80) return 6.0; if (v >= 70) return 7.0; return 8.0;
                case 2: if (v >= 80) return 6.0; if (v >= 70) return 7.0; if (v >= 60) return 8.0; if (v >= 50) return 9.0; return 10.0;
                case 3: default: if (v <= 20) return 14.0; if (v == 30) return 12.0; return 10.0;
            }
        }

        private void BtnProcesarPerfil_Click(object sender, RoutedEventArgs e)
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database; Editor ed = doc.Editor; CivilDocument civilDoc = CivilApplication.ActiveDocument;

            if (CmbSuperficies.SelectedItem == null) { CargarSuperficiesEnComboBox(); return; }
            ObjectId surfaceId = (ObjectId)((ComboBoxItem)CmbSuperficies.SelectedItem).Tag;

            ObjectId alignId = ObtenerEjeSeleccionado(db, civilDoc);
            if (alignId == ObjectId.Null) {
                MessageBox.Show("No se encontró el Eje Central válido.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double vtr = ObtenerVelocidadDiseno();

            int catIdx = CmbCategoriaVia.SelectedIndex;
            double pMax = ObtenerPendienteMaximaINVIAS(catIdx, vtr);

            // Tabla 4.3 INVIAS (longitud mínima de tangente vertical) — ahora cubre 20 a 130 km/h.
            // Antes, para Vtr > 80 km/h el valor caía a un valor por defecto de 60 m, apretando
            // los PIV y provocando choques/solapes al insertar las curvas verticales.
            double lMinTangente = ObtenerLongitudMinimaTangenteVertical(vtr);

            // Tabla 4.4 INVIAS - Parámetro K mínimo (antes los valores de curva cóncava estaban
            // sub-valorados en todos los casos y no había datos para Vtr > 70 km/h).
            double kCrest = ObtenerKMinimo(vtr, convexa: true);
            double kSag = ObtenerKMinimo(vtr, convexa: false);
            double lMinCurva = ObtenerLongitudMinimaCurvaVertical(vtr);

            PromptPointOptions ppo = new PromptPointOptions("\n[INVIAS] Haga clic para dibujar el PERFIL C3D: ");
            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK) return;

            using (DocumentLock docLock = doc.LockDocument())
            {
                try
                {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Alignment? alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment;
                    Autodesk.Civil.DatabaseServices.Surface? surface = tr.GetObject(surfaceId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                    if (alignment == null || surface == null) return;

                    ObjectId profileStyleId = civilDoc.Styles.ProfileStyles.Count > 0 ? civilDoc.Styles.ProfileStyles[0] : ObjectId.Null;
                    ObjectId profileLabelSetId = civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles.Count > 0 ? civilDoc.Styles.LabelSetStyles.ProfileLabelSetStyles[0] : ObjectId.Null;
                    ObjectId profileViewStyleId = civilDoc.Styles.ProfileViewStyles.Count > 0 ? civilDoc.Styles.ProfileViewStyles[0] : ObjectId.Null;
                    ObjectId bandSetStyleId = civilDoc.Styles.ProfileViewBandSetStyles.Count > 0 ? civilDoc.Styles.ProfileViewBandSetStyles[0] : ObjectId.Null;

                    // Sufijo único por ejecución: antes el nombre del perfil TN, de la rasante y de
                    // la vista de perfil dependían SOLO del nombre del eje ("TN_<eje>",
                    // "Rasante_INVIAS_<eje>", "Perfil_<eje>"). Al volver a pulsar este botón sobre el
                    // mismo eje (p. ej. después de editar la planta), Civil3D rechazaba el nombre
                    // duplicado con una excepción que no estaba capturada, y eso terminaba cerrando
                    // AutoCAD. Con el sufijo, cada ejecución crea un perfil y una vista nuevos, y el
                    // selector CmbPerfiles permite elegir después cuál usar para la memoria.
                    string sufijoPerfil = DateTime.Now.ToString("HHmmssfff");

                    string profileTNName = "TN_" + alignment.Name + "_" + sufijoPerfil;
                    if (!TryCrearPerfilDesdeSuperficie(civilDoc, profileTNName, alignId, alignment.Name, surfaceId, surface.Name, ObtenerNombreCapaActual(tr, db), db.Clayer, profileStyleId, profileLabelSetId, out ObjectId profileTNId, out string errorTN))
                        throw new System.Exception($"No se pudo crear el perfil del terreno natural: {errorTN}");
                    Profile? profileTN = tr.GetObject(profileTNId, OpenMode.ForWrite) as Profile;
                    if (profileTN != null) profileTN.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, 3);

                    ObjectId layoutStyleId = civilDoc.Styles.ProfileStyles.Count > 1 ? civilDoc.Styles.ProfileStyles[1] : profileStyleId;
                    string rasanteName = "Rasante_INVIAS_" + alignment.Name + "_" + sufijoPerfil;
                    if (!TryCrearPerfilPorDiseno(civilDoc, rasanteName, alignId, alignment.Name, ObtenerNombreCapaActual(tr, db), db.Clayer, layoutStyleId, profileLabelSetId, out ObjectId rasanteId, out string errorRasante))
                        throw new System.Exception($"No se pudo crear la rasante: {errorRasante}");
                    Profile? rasante = tr.GetObject(rasanteId, OpenMode.ForWrite) as Profile;
                    if (rasante != null) rasante.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, 1);

                    int curvasGeneradas = 0;
                    int curvasFallidas = 0;
                    int curvasAcortadas = 0;
                    List<string> avisosCurvas = new List<string>();
                    List<string> avisosAcortadas = new List<string>();

                    if (profileTN != null && rasante != null)
                    {
                        List<PviDefinition> pviList = new List<PviDefinition>();
                        pviList.Add(new PviDefinition { Station = alignment.StartingStation, Elevation = profileTN.ElevationAt(alignment.StartingStation), CurveLength = 0.0 });

                        List<double> sampledStations = new List<double>();
                        double currentSt = alignment.StartingStation + lMinTangente;
                        while (currentSt <= alignment.EndingStation - lMinTangente) { sampledStations.Add(currentSt); currentSt += lMinTangente; }

                        for (int i = 0; i < sampledStations.Count; i++)
                        {
                            // IMPORTANTE: zPrev/stPrev se toman del punto YA ENCADENADO en pviList
                            // (posiblemente ajustado por pMax en la iteración anterior), no de una
                            // nueva lectura de la superficie de TN. Antes se releía el TN cada vez,
                            // lo que podía dejar la pendiente de entrada (g1) inconsistente con la
                            // cota realmente asignada al PVI anterior, y por tanto con la curva que
                            // Civil3D construye de verdad entre ambos vértices.
                            PviDefinition anterior = pviList[pviList.Count - 1];
                            double stPrev = anterior.Station; double zPrev = anterior.Elevation;

                            double stCurr = sampledStations[i]; double zCurr = profileTN.ElevationAt(stCurr);
                            double stNext = (i == sampledStations.Count - 1) ? alignment.EndingStation : sampledStations[i + 1]; double zNext = profileTN.ElevationAt(stNext);

                            double g1 = ((zCurr - zPrev) / (stCurr - stPrev)) * 100.0;

                            // El recorte por pMax se aplica ANTES de calcular g1 definitivo y A,
                            // de modo que la decisión de generar curva (y su longitud) se basa en
                            // las mismas pendientes que tendrá el perfil realmente construido.
                            if (g1 > pMax) { zCurr = zPrev + (pMax / 100.0) * (stCurr - stPrev); g1 = pMax; }
                            else if (g1 < -pMax) { zCurr = zPrev - (pMax / 100.0) * (stCurr - stPrev); g1 = -pMax; }

                            double g2 = ((zNext - zCurr) / (stNext - stCurr)) * 100.0;

                            double A = Math.Abs(g2 - g1);
                            double calculatedLv = 0.0;

                            if (A >= 0.5) {
                                double kAplicado = (g1 > g2) ? kCrest : kSag;
                                calculatedLv = Math.Max(kAplicado * A, lMinCurva);
                            }
                            pviList.Add(new PviDefinition { Station = stCurr, Elevation = zCurr, CurveLength = calculatedLv });
                        }
                        {
                            PviDefinition ultimo = pviList[pviList.Count - 1];
                            double zFinal = profileTN.ElevationAt(alignment.EndingStation);
                            pviList.Add(new PviDefinition { Station = alignment.EndingStation, Elevation = zFinal, CurveLength = 0.0 });
                        }

                        // INSERCIÓN DE PVIs: se calcula el espacio realmente disponible frente al PVT
                        // ya insertado (PVI anterior) y al siguiente PIV. Si Civil3D rechaza la longitud
                        // propuesta (curvas solapadas), se reintenta con pasos más cortos, pero NUNCA por
                        // debajo del mínimo normativo absoluto (lMinCurva, Tabla 4.4). Una curva que solo
                        // cupo acortada por debajo de lo que exige K = L/A (numeral 4.2.3) queda instalada
                        // -para no dejar un quiebre recto donde sí cabía una curva- pero se marca e informa
                        // explícitamente como NO conforme, en vez de quedar oculta como un simple "éxito".
                        const double FACTOR_MARGEN_SEGURIDAD = 0.95; // margen para no tocar exactamente el PVT/PIV vecino
                        const double FACTOR_REINTENTO = 0.85;        // reducción por intento cuando Civil3D rechaza la longitud
                        RegistroCurvasVerticales.Remove(alignment.Name);

                        double pvtAnterior = alignment.StartingStation;
                        for (int idx = 0; idx < pviList.Count; idx++)
                        {
                            PviDefinition pviDef = pviList[idx];
                            bool esExtremo = (idx == 0 || idx == pviList.Count - 1);

                            if (!esExtremo && pviDef.CurveLength >= lMinCurva)
                            {
                                double longitudRequeridaPorK = pviDef.CurveLength;
                                double stSiguiente = pviList[idx + 1].Station;
                                double espacioDisponible = Math.Min(pviDef.Station - pvtAnterior, stSiguiente - pviDef.Station) * 2.0 * FACTOR_MARGEN_SEGURIDAD;
                                double lvIntentar = Math.Min(longitudRequeridaPorK, Math.Max(espacioDisponible, 0.0));
                                bool colocada = false;

                                while (lvIntentar >= lMinCurva && !colocada)
                                {
                                    try {
                                        rasante.PVIs.AddPVISymParabola(pviDef.Station, pviDef.Elevation, lvIntentar);
                                        pvtAnterior = pviDef.Station + lvIntentar / 2.0;
                                        colocada = true;
                                        curvasGeneradas++;
                                        RegistrarCurvaVertical(alignment.Name, pviDef.Station, lvIntentar);

                                        if (lvIntentar < longitudRequeridaPorK - 0.01)
                                        {
                                            curvasAcortadas++;
                                            avisosAcortadas.Add($"• PIV {FormatearAbscisa(pviDef.Station)}: curva instalada con {lvIntentar:F2} m (requería {longitudRequeridaPorK:F2} m por el criterio K de la Tabla 4.4); NO CUMPLE la distancia de visibilidad de parada. Revise/alargue la tangente vertical adyacente.");
                                        }
                                    } catch {
                                        lvIntentar *= FACTOR_REINTENTO;
                                    }
                                }

                                if (!colocada)
                                {
                                    rasante.PVIs.AddPVI(pviDef.Station, pviDef.Elevation);
                                    pvtAnterior = pviDef.Station;
                                    curvasFallidas++;
                                    avisosCurvas.Add($"• PIV {FormatearAbscisa(pviDef.Station)}: sin espacio suficiente entre tangentes verticales; quedó como quiebre recto.");
                                }
                            }
                            else
                            {
                                rasante.PVIs.AddPVI(pviDef.Station, pviDef.Elevation);
                                pvtAnterior = pviDef.Station;
                            }
                        }

                    }

                    string profileViewName = "Perfil_" + alignment.Name + "_" + sufijoPerfil;
                    if (!TryCrearVistaDePerfil(civilDoc, alignId, ppr.Value, profileViewName, bandSetStyleId, profileViewStyleId, out string errorVista))
                        throw new System.Exception($"No se pudo crear la vista de perfil: {errorVista}");
                    tr.Commit();

                    string mensaje = $"🚀 ¡Rasante Creada Exitosamente! ({rasanteName})\n\n• Curvas verticales generadas: {curvasGeneradas}";
                    bool hayIncidencias = curvasFallidas > 0 || curvasAcortadas > 0;
                    if (hayIncidencias)
                    {
                        if (curvasFallidas > 0)
                            mensaje += $"\n• PIV sin ninguna curva por falta de espacio: {curvasFallidas}";
                        if (curvasAcortadas > 0)
                            mensaje += $"\n• Curvas instaladas pero ACORTADAS por falta de espacio (no cumplen K mínimo): {curvasAcortadas}";
                        mensaje += "\n\n" + string.Join("\n", avisosCurvas) + "\n" + string.Join("\n", avisosAcortadas) +
                                   "\n\nRevise esos vértices: puede requerirse alargar la tangente vertical o suavizar la línea de rasante.";
                        MessageBox.Show(mensaje, "Rasante - Revisar", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    else
                    {
                        MessageBox.Show(mensaje, "INVIAS", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show($"Ocurrió un error al construir el perfil/rasante: {ex.Message}\n\nSi el eje fue editado después de un intento anterior, verifique que no queden entidades inconsistentes (perfiles o vistas huérfanas) y vuelva a intentarlo.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        // ==========================================
        // 🔹 PESTAÑA 3: TRANSVERSALES
        // ==========================================
        private void BtnProcesarTransversal_Click(object sender, RoutedEventArgs e)
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;
            CivilDocument civilDoc = CivilApplication.ActiveDocument;

            ObjectId alignId = ObtenerEjeSeleccionado(db, civilDoc);
            if (alignId == ObjectId.Null) {
                MessageBox.Show("No se encontró el Eje Central válido.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double anchoCarril = ObtenerAnchoCarril();
            double vtr = ObtenerVelocidadDiseno();
            int catIdx = CmbCategoriaVia.SelectedIndex;
            double eMaxProyecto = ObtenerPeralteMaximoProyecto(catIdx);
            double deltaSMax = ObtenerDeltaSMaximo(vtr);
            double lVehSobreancho = ObtenerLongitudVehiculoSobreancho();
            bool esArticulado3S2 = CmbVehiculo.SelectedIndex == 2;

            PromptPointOptions ppo = new PromptPointOptions("\n[INVIAS] Haga clic para insertar TABLA y VISTA DE PERALTES NATIVA: ");
            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK) return;

            using (DocumentLock docLock = doc.LockDocument())
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    Alignment? alignment = tr.GetObject(alignId, OpenMode.ForWrite) as Alignment;
                    if (alignment == null) return;

                    List<CurveData> curves = new List<CurveData>();
                    int arcIdx = 1;

                    // Se aplana primero TODO el eje (líneas, arcos y espirales, incluidas las
                    // agrupadas en entidades compuestas Espiral-Curva-Espiral) para poder mirar,
                    // al llegar a un arco, si la entidad inmediatamente anterior/siguiente es
                    // una espiral de transición real dibujada en el eje.
                    List<AlignmentEntity> hojas = new List<AlignmentEntity>();
                    foreach (AlignmentEntity entity in alignment.Entities)
                        hojas.AddRange(AplanarEntidadAlineamiento(entity));

                    for (int idx = 0; idx < hojas.Count; idx++)
                    {
                        if (hojas[idx] is AlignmentArc arc)
                        {
                            double R = arc.Radius;
                            double deltaGrados = arc.Delta * (180.0 / Math.PI);
                            bool aplicaSobreancho = RequiereSobreanchoCurva(R, anchoCarril * 2.0, deltaGrados);
                            double S = !aplicaSobreancho ? 0.0
                                : esArticulado3S2 ? CalcularSobreanchoArticulado(R, vtr, anchoCarril * 2.0)
                                : CalcularSobreancho(R, lVehSobreancho);

                            // Numeral 3.1.3.5 INVIAS (Método 5): peralte específico de ESTA curva según
                            // su Radio adoptado (Tablas 3.4/3.5), no forzado al máximo del proyecto.
                            double eCurva = ObtenerPeraltePorRadio(vtr, R, catIdx);

                            bool tieneEspiral = false;
                            double leEntrada = 0, leSalida = 0;
                            if (idx > 0 && TryExtraerDatosEspiral(hojas[idx - 1], out double lPrev, out _, out _, out _)) { leEntrada = lPrev; tieneEspiral = true; }
                            if (idx < hojas.Count - 1 && TryExtraerDatosEspiral(hojas[idx + 1], out double lNext, out _, out _, out _)) { leSalida = lNext; tieneEspiral = true; }

                            // Numeral 3.2.2 INVIAS: en curvas circulares simples, Lt se calcula con la
                            // pendiente relativa máxima de la rampa (Tabla 3.6) y el peralte específico
                            // de la curva (eCurva). En curvas espiralizadas (numeral 3.2.2.2 y 5.4.2), la
                            // transición del peralte y del sobreancho se desarrolla linealmente en la
                            // longitud REAL de la espiral (Le) ya dibujada en el eje.
                            double Lt = tieneEspiral ? Math.Max(leEntrada, leSalida) : CalcularLongitudTransicionPeralte(anchoCarril, eCurva, deltaSMax);

                            // Numeral 3.3 INVIAS: parámetro A adoptado (A = √(Rc·Le)) frente al mínimo
                            // (envolvente de los criterios I, II, III) y al máximo (1.1·Rc).
                            double aEntrada = leEntrada > 0 ? Math.Sqrt(R * leEntrada) : 0.0;
                            double aSalida = leSalida > 0 ? Math.Sqrt(R * leSalida) : 0.0;
                            double aMinNorm = tieneEspiral ? CalcularAMinimoEspiral(vtr, R, eCurva, anchoCarril, deltaSMax) : 0.0;
                            double aMaxNorm = tieneEspiral ? CalcularAMaximoEspiral(R) : 0.0;

                            double v1x = arc.StartPoint.X - arc.CenterPoint.X; double v1y = arc.StartPoint.Y - arc.CenterPoint.Y;
                            double v2x = arc.EndPoint.X - arc.CenterPoint.X; double v2y = arc.EndPoint.Y - arc.CenterPoint.Y;
                            bool isRight = ((v1x * v2y) - (v1y * v2x)) < 0;

                            curves.Add(new CurveData {
                                Elem = $"C-{arcIdx++}", StartSt = arc.StartStation, EndSt = arc.EndStation,
                                Radius = R, S_max = S, RequiereSobreancho = aplicaSobreancho, E_max = eCurva, Lt = Lt, IsRight = isRight,
                                Delta = deltaGrados, Length = arc.Length, TieneEspiral = tieneEspiral, LeEntrada = leEntrada, LeSalida = leSalida,
                                AEntrada = aEntrada, ASalida = aSalida, AMinNormativo = aMinNorm, AMaxNormativo = aMaxNorm
                            });
                        }
                    }

                    ObjectId styleId = civilDoc.Styles.AlignmentStyles.Count > 0 ? civilDoc.Styles.AlignmentStyles[0] : ObjectId.Null;
                    try
                    {
                        string sufix = DateTime.Now.ToString("HHmmss");
                        if (!TryCrearAlignmentOffset(alignId, alignment.Name + "_Izdo_" + sufix, -anchoCarril, styleId, alignment.StartingStation, alignment.EndingStation, out ObjectId leftOffsetId, out string errorLeft))
                            throw new System.Exception($"No se pudo crear el alineamiento de desfase izquierdo: {errorLeft}");
                        if (!TryCrearAlignmentOffset(alignId, alignment.Name + "_Dcho_" + sufix, anchoCarril, styleId, alignment.StartingStation, alignment.EndingStation, out ObjectId rightOffsetId, out string errorRight))
                            throw new System.Exception($"No se pudo crear el alineamiento de desfase derecho: {errorRight}");

                        Alignment? leftAlign = tr.GetObject(leftOffsetId, OpenMode.ForWrite) as Alignment;
                        Alignment? rightAlign = tr.GetObject(rightOffsetId, OpenMode.ForWrite) as Alignment;

                        if (leftAlign != null && rightAlign != null)
                        {
                            // Numeral 3.2.2.1 INVIAS: fracción de Lt que se desarrolla dentro de la
                            // curva (resto en tangente), configurable entre 20% y 40% (equivalente al
                            // 60%-80% en tangente permitido por el Manual).
                            double fraccionEnCurva = 1.0 - (ObtenerPorcentajeTransicionEnTangente() / 100.0);

                            foreach (var c in curves)
                            {
                                if (!c.RequiereSobreancho) continue;

                                double stFullStart = c.StartSt + fraccionEnCurva * c.Lt;
                                double stFullEnd   = c.EndSt - fraccionEnCurva * c.Lt;
                                if (stFullStart >= stFullEnd) { double mid = (c.StartSt + c.EndSt) / 2.0; stFullStart = mid - 0.5; stFullEnd = mid + 0.5; }

                                Alignment targetAlign = c.IsRight ? rightAlign : leftAlign;
                                double targetWidth = anchoCarril + c.S_max;

                                try {
                                    targetAlign.OffsetAlignmentInfo.AddWidening(stFullStart, stFullEnd, targetWidth);
                                    dynamic offsetInfo = targetAlign.OffsetAlignmentInfo;

                                    // La región creada por AddWidening no siempre reporta su StartStation
                                    // idéntico al valor solicitado (Civil3D puede reencuadrarla contra
                                    // regiones vecinas). En vez de exigir una coincidencia exacta, se
                                    // vuelca el estado de TODAS las regiones a la ventana de comandos y
                                    // se ajusta la que tenga el ancho más parecido al objetivo, dentro de
                                    // una ventana amplia de estaciones alrededor de la curva.
                                    ed.WriteMessage($"\n[INVIAS] Diagnóstico offset {(c.IsRight ? "derecho" : "izquierdo")} para {c.Elem} (Lt objetivo = {c.Lt:F2} m, ancho objetivo = {targetWidth:F2} m):");
                                    dynamic mejorRegion = null;
                                    double mejorDiferenciaAncho = double.MaxValue;
                                    int idxRegion = 0;
                                    foreach (dynamic region in offsetInfo.Regions)
                                    {
                                        double regStart = double.NaN, regEnd = double.NaN, regWidth = double.NaN;
                                        try { regStart = (double)region.StartStation; } catch { }
                                        try { regEnd = (double)region.EndStation; } catch { }
                                        try { regWidth = (double)region.Width; } catch { try { regWidth = (double)region.Offset; } catch { } }
                                        ed.WriteMessage($"\n   Región {idxRegion++}: Inicio={regStart:F2} Fin={regEnd:F2} Ancho={regWidth:F2}");

                                        bool dentroDeVentana = !double.IsNaN(regStart) && !double.IsNaN(regEnd) &&
                                                                regStart < c.EndSt + 2.0 * c.Lt && regEnd > c.StartSt - 2.0 * c.Lt;
                                        if (dentroDeVentana && !double.IsNaN(regWidth)) {
                                            double diff = Math.Abs(regWidth - targetWidth);
                                            if (diff < mejorDiferenciaAncho) { mejorDiferenciaAncho = diff; mejorRegion = region; }
                                        }
                                    }

                                    bool transicionAjustada = false;
                                    if (mejorRegion != null) {
                                        try {
                                            dynamic entry = mejorRegion.EntryTransition;
                                            if (entry != null) {
                                                entry.Length = c.Lt;
                                                transicionAjustada = true;
                                            }
                                        } catch (System.Exception exEntry) {
                                            ed.WriteMessage($"\n[INVIAS] Aviso: fallo al fijar EntryTransition.Length en {c.Elem}: {exEntry.Message}");
                                        }
                                        try {
                                            dynamic exit = mejorRegion.ExitTransition;
                                            if (exit != null) {
                                                exit.Length = c.Lt;
                                                transicionAjustada = true;
                                            }
                                        } catch (System.Exception exExit) {
                                            ed.WriteMessage($"\n[INVIAS] Aviso: fallo al fijar ExitTransition.Length en {c.Elem}: {exExit.Message}");
                                        }
                                    }

                                    if (!transicionAjustada)
                                        ed.WriteMessage($"\n[INVIAS] Aviso: la curva {c.Elem} quedó con transición de sobreancho por defecto (longitud 0). Revise el diagnóstico de regiones anterior y ajuste manualmente en el editor de Offset Alignment (Geometry Editor > Widenings).");
                                } catch (System.Exception exWiden) {
                                    ed.WriteMessage($"\n[INVIAS] Aviso: no se pudo generar el sobreancho en {c.Elem} ({exWiden.Message}).");
                                }
                            }
                        }
                    }
                    catch { MessageBox.Show("Se produjo un conflicto al generar los desfases. Compruebe la geometría del eje.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning); }

                    CalcularPeraltesNativosCivil3D(alignment, curves);

                    try
                    {
                        Type viewType = typeof(SuperelevationView);
                        System.Reflection.MethodInfo[] methods = viewType.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

                        foreach (var mi in methods)
                        {
                            if (mi.Name == "Create")
                            {
                                var p = mi.GetParameters();
                                if (p.Length == 3 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(ObjectId) && p[2].ParameterType == typeof(Point3d))
                                {
                                    Point3d superViewPt = new Point3d(ppr.Value.X, ppr.Value.Y - 50.0, ppr.Value.Z);
                                    mi.Invoke(null, new object[] { "Vista_" + alignment.Name, alignId, superViewPt });
                                    break;
                                }
                                else if (p.Length == 4 && p[0].ParameterType == typeof(ObjectId) && p[1].ParameterType == typeof(string) && p[2].ParameterType == typeof(ObjectId) && p[3].ParameterType == typeof(Point3d))
                                {
                                    ObjectId sViewStyleId = civilDoc.Styles.SuperelevationViewStyles.Count > 0 ? civilDoc.Styles.SuperelevationViewStyles[0] : ObjectId.Null;
                                    Point3d superViewPt = new Point3d(ppr.Value.X, ppr.Value.Y - 50.0, ppr.Value.Z);
                                    mi.Invoke(null, new object[] { alignId, "Vista_" + alignment.Name, sViewStyleId, superViewPt });
                                    break;
                                }
                            }
                        }
                    } catch { }
                    tr.Commit();
                    int nCurvasConSobreancho = curves.Count(c => c.RequiereSobreancho);
                    MessageBox.Show($"🚀 ¡Transiciones y Peraltes C3D Generados!\n\n• Curvas con sobreancho (R < 160 m): {nCurvasConSobreancho} de {curves.Count}\n• Longitud de transición Lt aplicada según numeral 3.2.2 INVIAS.\n• Revise la ventana de comandos por avisos de curvas específicas.", "INVIAS", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void CalcularPeraltesNativosCivil3D(Alignment alignment, List<CurveData> curves)
        {
            SuperelevationCrossSegmentType leftLane = (SuperelevationCrossSegmentType)0;
            SuperelevationCrossSegmentType rightLane = (SuperelevationCrossSegmentType)1;

            foreach (string name in Enum.GetNames(typeof(SuperelevationCrossSegmentType)))
            {
                if (name.ToLower().Contains("left") && (name.ToLower().Contains("out") || name.ToLower().Contains("ext")) && !name.ToLower().Contains("shoulder") && !name.ToLower().Contains("inside"))
                    leftLane = (SuperelevationCrossSegmentType)Enum.Parse(typeof(SuperelevationCrossSegmentType), name);

                if (name.ToLower().Contains("right") && (name.ToLower().Contains("out") || name.ToLower().Contains("ext")) && !name.ToLower().Contains("shoulder") && !name.ToLower().Contains("inside"))
                    rightLane = (SuperelevationCrossSegmentType)Enum.Parse(typeof(SuperelevationCrossSegmentType), name);
            }

            // Numeral 3.2.2.1 INVIAS: fracción de Lt que se desarrolla dentro de la curva
            // (resto en tangente), configurable entre 20% y 40%.
            double fraccionEnCurva = 1.0 - (ObtenerPorcentajeTransicionEnTangente() / 100.0);

            try
            {
                int curveIndex = 0;
                foreach (SuperelevationCurve supCurve in alignment.SuperelevationCurves)
                {
                    if (curveIndex >= curves.Count) break;
                    CurveData c = curves[curveIndex];

                    for (int i = supCurve.CriticalStations.Count - 1; i >= 0; i--) {
                        try { supCurve.CriticalStations.RemoveAt(i); } catch { }
                    }

                    double st0 = c.StartSt - (1.0 - fraccionEnCurva) * c.Lt;
                    double st1 = c.StartSt + fraccionEnCurva * c.Lt;
                    double st2 = c.EndSt - fraccionEnCurva * c.Lt;
                    double st3 = c.EndSt + (1.0 - fraccionEnCurva) * c.Lt;

                    double eDec = c.E_max / 100.0;
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
                            cs.SetSlope(leftLane, c.IsRight ? outS : inS);
                            cs.SetSlope(rightLane, c.IsRight ? inS : outS);
                        }
                    }
                    #pragma warning restore CS0618

                    curveIndex++;
                }
            }
            catch { }
        }

        // ==========================================
        // 🔹 PESTAÑA 4: EXPORTACIÓN DE MEMORIA (CON EXTRACCIÓN DE CURVAS VERTICALES CORREGIDA)
        // ==========================================
        private void BtnExportarMemoria_Click(object sender, RoutedEventArgs e)
        {
            Document? doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Database db = doc.Database;
            Editor ed = doc.Editor;
            CivilDocument civilDoc = CivilApplication.ActiveDocument;

            ObjectId alignId = ObtenerEjeSeleccionado(db, civilDoc);
            if (alignId == ObjectId.Null) {
                MessageBox.Show("No se encontró el Eje Central válido para generar la memoria.", "Atención", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog();
            saveFileDialog.Filter = "Documento de Word (*.doc)|*.doc";
            saveFileDialog.Title = "Guardar Memoria de Diseño INVIAS";
            saveFileDialog.FileName = "Memoria_Calculo_INVIAS.doc";

            if (saveFileDialog.ShowDialog() == true)
            {
                string filepath = saveFileDialog.FileName;

                try
                {
                    List<CurveData> curves = new List<CurveData>();
                    List<PviData> pvis = new List<PviData>();
                    List<PIData> pis = new List<PIData>();
                    List<AlignElem> alignElements = new List<AlignElem>();

                    string alignmentName = "";
                    double vtr = 30.0;
                    double anchoCarril = 3.65;
                    double L_vehiculo = 10.5;
                    double pMax = 0.0;

                    using (DocumentLock docLock = doc.LockDocument())
                    {
                        using (Transaction tr = db.TransactionManager.StartTransaction())
                        {
                            Alignment? alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment;
                            if (alignment == null) return;

                            alignmentName = alignment.Name;
                            anchoCarril = ObtenerAnchoCarril();
                            vtr = ObtenerVelocidadDiseno();
                            L_vehiculo = ObtenerLongitudVehiculoDiseno();
                            int catIdx = CmbCategoriaVia.SelectedIndex;

                            double rMinCalculado = CalcularRadioMinimo(vtr, catIdx);
                            double fMax = CalcularFriccionMaxima(vtr);
                            double eMaxProyecto = ObtenerPeralteMaximoProyecto(catIdx);
                            double deltaSMax = ObtenerDeltaSMaximo(vtr);
                            double lVehSobreancho = ObtenerLongitudVehiculoSobreancho();
                            bool esArticulado3S2 = CmbVehiculo.SelectedIndex == 2;
                            pMax = ObtenerPendienteMaximaINVIAS(catIdx, vtr);

                            int arcIdx = 1, lineIdx = 1, espIdx = 1, piCounter = 1;

                            // EXTRACCIÓN PLANTA — se aplana el eje (líneas, arcos y espirales,
                            // incluidas las agrupadas en entidades compuestas Espiral-Curva-Espiral)
                            // para poder trabajar con CUALQUIER eje del dibujo, no solo los generados
                            // por la Pestaña 1 (que nunca inserta espirales de transición).
                            List<AlignmentEntity> hojasPlanta = new List<AlignmentEntity>();
                            foreach (AlignmentEntity entity in alignment.Entities)
                                hojasPlanta.AddRange(AplanarEntidadAlineamiento(entity));

                            for (int idx = 0; idx < hojasPlanta.Count; idx++)
                            {
                                AlignmentEntity hoja = hojasPlanta[idx];
                                if (hoja is AlignmentLine line)
                                {
                                    double dE = line.EndPoint.X - line.StartPoint.X;
                                    double dN = line.EndPoint.Y - line.StartPoint.Y;
                                    double azRad = Math.Atan2(dE, dN);
                                    if (azRad < 0) azRad += 2.0 * Math.PI;

                                    alignElements.Add(new AlignElem {
                                        Elem = $"T-{lineIdx++}", Type = "Recta", StartSt = line.StartStation,
                                        EndSt = line.EndStation, Length = line.Length,
                                        Parameter = "Azimut: " + FormatearGMS(azRad * (180.0 / Math.PI))
                                    });
                                }
                                else if (hoja is AlignmentArc arc)
                                {
                                    double R = arc.Radius;
                                    double deltaGrados = arc.Delta * (180.0 / Math.PI);
                                    bool aplicaSobreancho = RequiereSobreanchoCurva(R, anchoCarril * 2.0, deltaGrados);
                                    double S = !aplicaSobreancho ? 0.0
                                        : esArticulado3S2 ? CalcularSobreanchoArticulado(R, vtr, anchoCarril * 2.0)
                                        : CalcularSobreancho(R, lVehSobreancho);

                                    // Numeral 3.1.3.5 INVIAS (Método 5): peralte específico de ESTA curva según
                                    // su Radio adoptado (Tablas 3.4/3.5), no forzado al máximo del proyecto.
                                    double eCurva = ObtenerPeraltePorRadio(vtr, R, catIdx);

                                    bool tieneEspiral = false;
                                    double leEntrada = 0, leSalida = 0;
                                    if (idx > 0 && TryExtraerDatosEspiral(hojasPlanta[idx - 1], out double lPrev, out _, out _, out _)) { leEntrada = lPrev; tieneEspiral = true; }
                                    if (idx < hojasPlanta.Count - 1 && TryExtraerDatosEspiral(hojasPlanta[idx + 1], out double lNext, out _, out _, out _)) { leSalida = lNext; tieneEspiral = true; }

                                    // Numeral 3.2.2.2 / 5.4.2 INVIAS: en curvas espiralizadas, la transición
                                    // se desarrolla en la longitud REAL de la espiral dibujada (Le), no en el
                                    // valor teórico de la Tabla 3.6 (que solo aplica a curvas circulares simples).
                                    double Lt_Aplicada = tieneEspiral ? Math.Max(leEntrada, leSalida) : CalcularLongitudTransicionPeralte(anchoCarril, eCurva, deltaSMax);
                                    double asCalc = Lt_Aplicada > 0 ? (anchoCarril * eCurva) / Lt_Aplicada : 0.0;

                                    // Numeral 3.3 INVIAS: parámetro A adoptado (A = √(Rc·Le)) frente al mínimo
                                    // (envolvente de los criterios I, II, III) y al máximo (1.1·Rc).
                                    double aEntrada = leEntrada > 0 ? Math.Sqrt(R * leEntrada) : 0.0;
                                    double aSalida = leSalida > 0 ? Math.Sqrt(R * leSalida) : 0.0;
                                    double aMinNorm = tieneEspiral ? CalcularAMinimoEspiral(vtr, R, eCurva, anchoCarril, deltaSMax) : 0.0;
                                    double aMaxNorm = tieneEspiral ? CalcularAMaximoEspiral(R) : 0.0;

                                    double deltaRad = arc.Delta;
                                    double tangent = R * Math.Tan(deltaRad / 2.0);

                                    double v1x = arc.StartPoint.X - arc.CenterPoint.X;
                                    double v1y = arc.StartPoint.Y - arc.CenterPoint.Y;
                                    double v2x = arc.EndPoint.X - arc.CenterPoint.X;
                                    double v2y = arc.EndPoint.Y - arc.CenterPoint.Y;
                                    bool isRight = ((v1x * v2y) - (v1y * v2x)) < 0;

                                    double anglePC = Math.Atan2(v1y, v1x);
                                    double tangAngle = anglePC + (isRight ? -Math.PI / 2.0 : Math.PI / 2.0);
                                    double piX = arc.StartPoint.X + tangent * Math.Cos(tangAngle);
                                    double piY = arc.StartPoint.Y + tangent * Math.Sin(tangAngle);
                                    double piStation = arc.StartStation + tangent;

                                    pis.Add(new PIData {
                                        Id = piCounter++, Station = piStation, North = piY, East = piX, Delta = deltaRad * (180.0 / Math.PI)
                                    });

                                    string elemName = $"C-{arcIdx++}";
                                    curves.Add(new CurveData {
                                        Elem = elemName, StartSt = arc.StartStation, EndSt = arc.EndStation,
                                        Radius = R, S_max = S, RequiereSobreancho = aplicaSobreancho, E_max = eCurva, Lt = Lt_Aplicada, IsRight = isRight,
                                        Delta = deltaGrados, Length = arc.Length, Tangent = tangent,
                                        Vch = vtr, Ftmax = fMax, Rmin = rMinCalculado, AsMax = deltaSMax, AsCalc = asCalc, LtMin = Lt_Aplicada,
                                        TieneEspiral = tieneEspiral, LeEntrada = leEntrada, LeSalida = leSalida,
                                        AEntrada = aEntrada, ASalida = aSalida, AMinNormativo = aMinNorm, AMaxNormativo = aMaxNorm
                                    });

                                    alignElements.Add(new AlignElem {
                                        Elem = elemName, Type = tieneEspiral ? "Curva espiralizada" : "Curva", StartSt = arc.StartStation,
                                        EndSt = arc.EndStation, Length = arc.Length, Parameter = "Radio: " + R.ToString("F2") + "m"
                                    });
                                }
                                else if (TryExtraerDatosEspiral(hoja, out double leLen, out double aParam, out double eStartSt, out double eEndSt))
                                {
                                    alignElements.Add(new AlignElem {
                                        Elem = $"E-{espIdx++}", Type = "Espiral", StartSt = eStartSt,
                                        EndSt = eEndSt, Length = leLen, Parameter = aParam > 0 ? $"A = {aParam:F2}" : ""
                                    });
                                }
                            }

                            // EXTRACCIÓN RASANTE (BLINDADA PARA CURVAS VERTICALES)
                            // Se usa el perfil elegido explícitamente en CmbPerfiles; si no hay
                            // selección, se conserva como respaldo la heurística automática.
                            ObjectId perfilId = ObtenerPerfilSeleccionado(tr, alignment);
                            Profile? rasante = perfilId != ObjectId.Null ? tr.GetObject(perfilId, OpenMode.ForRead) as Profile : null;

                            if (rasante != null)
                            {
                                int pviCount = 1;
                                double kminConvexa = ObtenerKMinimo(vtr, convexa: true);
                                double kminConcava = ObtenerKMinimo(vtr, convexa: false);
                                double lMinCurva = ObtenerLongitudMinimaCurvaVertical(vtr);

                                // Atrapar todas las curvas (parábolas) dibujadas en la rasante
                                List<dynamic> profileCurves = new List<dynamic>();
                                try {
                                    foreach (dynamic ent in rasante.Entities) {
                                        string tName = ent.GetType().Name.ToLower();
                                        if (tName.Contains("parabola") || tName.Contains("curve")) {
                                            profileCurves.Add(ent);
                                        }
                                    }
                                } catch { }

                                // Omitir el primer y último punto (K0+000 y K Final nunca tienen curva vertical)
                                for (int i = 1; i < rasante.PVIs.Count - 1; i++)
                                {
                                    ProfilePVI pvi = rasante.PVIs[i];
                                    double sta = 0.0;
                                    #pragma warning disable CS0618
                                    try { sta = pvi.RawStation; } catch { try { sta = pvi.Station; } catch {} }
                                    #pragma warning restore CS0618

                                    double g1 = 0, g2 = 0, A = 0, Lv = 0, K = 0;
                                    string tipo = "Recto";

                                    try { g1 = pvi.GradeIn * 100.0; } catch {}
                                    try { g2 = pvi.GradeOut * 100.0; } catch {}

                                    A = Math.Abs(g2 - g1);
                                    if (A > 0.01) tipo = g1 > g2 ? "Convexa" : "Cóncava";

                                    // 1) Fuente primaria: la geometría REAL del perfil en este momento. Se
                                    // busca, entre las entidades de curva del perfil, la que esté centrada
                                    // más cerca de esta abscisa (en vez de tomar la primera que "contenga"
                                    // la estación). Esto refleja de inmediato cualquier edición manual que
                                    // el usuario haya hecho sobre la rasante (arrastrar un PVI, cambiar la
                                    // longitud de una curva con grips, etc.), sin depender de si el cambio
                                    // lo hizo este asistente o el usuario directamente en Civil3D.
                                    bool origenConfiable = false;
                                    double mejorDistancia = double.MaxValue;
                                    foreach (var curve in profileCurves) {
                                        try {
                                            double sStart = curve.StartStation;
                                            double sEnd = curve.EndStation;
                                            double centro = (sStart + sEnd) / 2.0;
                                            double dist = Math.Abs(sta - centro);
                                            if (dist < mejorDistancia && sta > sStart - 0.5 && sta < sEnd + 0.5) {
                                                mejorDistancia = dist;
                                                Lv = TryLeerLongitudCurvaVertical(curve, out double lvGeom) ? lvGeom : curve.Length;
                                                origenConfiable = true;
                                            }
                                        } catch { }
                                    }

                                    // 2) Respaldo: si no se pudo leer la geometría real (p. ej. nombres de
                                    // propiedad no reconocidos en esta versión de Civil3D), se usa el
                                    // registro de lo que ESTE asistente insertó en la Pestaña 2. Es un dato
                                    // exacto pero puede quedar desactualizado si el usuario edita después la
                                    // curva a mano sin mover la abscisa del PIV.
                                    if (!origenConfiable && TryObtenerCurvaVerticalRegistrada(alignment.Name, sta, out double lvRegistrada))
                                    {
                                        Lv = lvRegistrada;
                                        origenConfiable = true;
                                        ed.WriteMessage($"\n[INVIAS] Aviso: PIV {FormatearAbscisa(sta)} usa la longitud registrada por el asistente ({Lv:F2} m) porque no se pudo leer la geometría real de la curva; si la editó manualmente después de generarla, verifique este valor.");
                                    }

                                    // Si tampoco así se pudo confirmar la curva pero la diferencia de
                                    // pendientes exige una (A >= 0.5%), se estima con la fórmula normativa
                                    // y se avisa en la ventana de comandos para revisión manual, en vez de
                                    // reportar silenciosamente "Falta Curva" cuando pudo tratarse solo de
                                    // una lectura fallida de la geometría de Civil3D.
                                    if (Lv < 1.0 && A >= 0.5) {
                                        double kAplicado = (tipo == "Convexa") ? kminConvexa : kminConcava;
                                        Lv = Math.Max(kAplicado * A, lMinCurva);
                                        if (!origenConfiable)
                                            ed.WriteMessage($"\n[INVIAS] Aviso: PIV {FormatearAbscisa(sta)} (A={A:F2}%) requiere curva vertical; no se pudo confirmar su longitud real en el dibujo, se reporta el valor mínimo normativo estimado ({Lv:F2} m). Verifique manualmente en el editor de rasante.");
                                    }

                                    if (A > 0.01 && Lv > 0) K = Lv / A;

                                    pvis.Add(new PviData {
                                        Id = pviCount++, Station = sta, Elevation = pvi.Elevation,
                                        GradeIn = g1, GradeOut = g2, A = A, CurveLength = Lv, K = K, Type = tipo,
                                        KminNorma = (tipo == "Convexa" ? kminConvexa : kminConcava), LminNorma = lMinCurva
                                    });
                                }
                            }
                        }
                    }

                    string html = ConstruirEstructuraMemoriaTotal(alignmentName, curves, pvis, pis, alignElements, vtr, L_vehiculo, anchoCarril, pMax);
                    File.WriteAllText(filepath, html, Encoding.UTF8);

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filepath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Ocurrió un error al exportar la memoria: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private string ConstruirEstructuraMemoriaTotal(string nombreEje, List<CurveData> curves, List<PviData> pvis, List<PIData> pis, List<AlignElem> alignElements, double vtr, double vehiculo, double anchoCarril, double pMax)
        {
            string catVia = (CmbCategoriaVia.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "N/A";
            string terreno = (CmbTipoTerreno.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "N/A";
            string vDiseno = (CmbVehiculo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "N/A";
            int catIdx = CmbCategoriaVia.SelectedIndex;
            int terIdx = CmbTipoTerreno.SelectedIndex;
            bool esArticulado3S2 = CmbVehiculo.SelectedIndex == 2;

            double fl_long = 0.40;
            if (vtr >= 40) fl_long = 0.38; if (vtr >= 50) fl_long = 0.35; if (vtr >= 60) fl_long = 0.33;
            if (vtr >= 70) fl_long = 0.31; if (vtr >= 80) fl_long = 0.30; if (vtr >= 100) fl_long = 0.29;

            double f_max = CalcularFriccionMaxima(vtr);
            double eMaxProyecto = ObtenerPeralteMaximoProyecto(catIdx);
            double deltaSMax = ObtenerDeltaSMaximo(vtr);
            double r_min_calc = CalcularRadioMinimo(vtr, catIdx);
            double tpr = 2.5;
            double dp_calc = (0.278 * vtr * tpr) + (Math.Pow(vtr, 2) / (254 * fl_long));
            double dpTabla = ObtenerDistanciaVisibilidadParada(vtr);
            double l_min_vertical = ObtenerLongitudMinimaCurvaVertical(vtr);
            double lMinTangenteVert = ObtenerLongitudMinimaTangenteVertical(vtr);
            double kConvexaUsado = ObtenerKMinimo(vtr, true);
            double kConcavaUsado = ObtenerKMinimo(vtr, false);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<html xmlns:o='urn:schemas-microsoft-com:office:office' xmlns:w='urn:schemas-microsoft-com:office:word' xmlns='http://www.w3.org/TR/REC-html40'>");
            sb.AppendLine("<head><meta charset='utf-8'><title>Estudio de Trazado y Diseño Geométrico</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body { font-family: 'Arial', sans-serif; line-height: 1.5; color: #000; font-size: 11pt; }");
            sb.AppendLine("h1 { color: #000; font-size: 14pt; font-weight: bold; margin-bottom: 20px; }");
            sb.AppendLine("h2 { color: #000; font-size: 12pt; font-weight: bold; margin-top: 20px; }");
            sb.AppendLine("h3 { color: #000; font-size: 11pt; font-weight: bold; margin-top: 15px; margin-bottom: 5px; }");
            sb.AppendLine("h4 { color: #000; font-size: 10.5pt; font-weight: bold; margin-top: 12px; margin-bottom: 4px; }");
            sb.AppendLine("h5 { color: #000; font-size: 10.5pt; font-weight: bold; font-style: italic; margin-top: 10px; margin-bottom: 4px; }");
            sb.AppendLine("p { text-align: justify; margin-bottom: 10px; margin-top: 5px; }");
            sb.AppendLine("table { border-collapse: collapse; width: 100%; margin-top: 10px; margin-bottom: 15px; font-size: 9.5pt; }");
            sb.AppendLine("th, td { border: 1px solid #000; padding: 5px; text-align: center; vertical-align: middle; }");
            sb.AppendLine("th { font-weight: bold; background-color: #f2f2f2; }");
            sb.AppendLine(".formula-box { font-family: 'Cambria Math', serif; font-size: 12pt; text-align: center; margin: 15px 0; background-color:#f7f7f7; padding:8px; border:1px solid #ccc;}");
            sb.AppendLine(".cita { font-size: 9pt; font-style: italic; color:#333; }");
            sb.AppendLine(".cumple { color: #000; }");
            sb.AppendLine(".nocumple { color: #000; font-weight: bold; }");
            sb.AppendLine("</style></head><body>");

            sb.AppendLine($"<h1>ESTUDIO DE TRAZADO Y DISEÑO GEOMÉTRICO<br>TRAMO {nombreEje.ToUpper()}</h1>");

            sb.AppendLine("<h2>1. INTRODUCCIÓN</h2>");
            sb.AppendLine("<p>En el presente Capítulo, se redacta la información obtenida para el diseño geométrico de la vía, teniendo como objetivo la definición geométrica del trazado propuesto, así como la verificación del cumplimiento, por parte de dicho trazado, de las normas y criterios establecidos en el vigente Manual de Diseño Geométrico de Carreteras del INVIAS 2008.</p>");
            sb.AppendLine("<p>La presente memoria consigna, para cada elemento geométrico generado en el modelo de Civil 3D, el criterio normativo aplicado, la fórmula empleada y el resultado de la verificación de cumplimiento, de manera que el documento sea auditable elemento a elemento frente al Manual.</p>");
            sb.AppendLine("<h3>1.1. Metodología y fuentes normativas</h3>");
            sb.AppendLine("<p>La verificación geométrica de este documento se apoya en las siguientes tablas y numerales del Manual de Diseño Geométrico de Carreteras INVIAS 2008, cada una reproducida junto al resultado del proyecto en el numeral correspondiente de esta memoria:</p>");
            sb.AppendLine("<ul>");
            sb.AppendLine("<li><b>Numeral 2.1.2 y Tabla 2.1:</b> velocidad de diseño del tramo homogéneo (V<sub>TR</sub>), adoptada como el extremo superior del rango normativo por categoría y terreno.</li>");
            sb.AppendLine("<li><b>Numeral 2.2 y Figuras 2.2 a 2.7:</b> dimensiones de carrocería y ángulo máximo de dirección de los vehículos de diseño tipo.</li>");
            sb.AppendLine("<li><b>Numeral 3.1.3.3 y Tabla 3.1:</b> coeficiente de fricción transversal máxima (f<sub>Tmáx</sub>).</li>");
            sb.AppendLine("<li><b>Numeral 3.1.3.2:</b> peralte máximo del proyecto (e<sub>máx</sub>).</li>");
            sb.AppendLine("<li><b>Numeral 3.1.3.4 y Tablas 3.2/3.3:</b> radio mínimo de curvatura (R<sub>Cmín</sub>).</li>");
            sb.AppendLine("<li><b>Numeral 3.1.3.5 y Tablas 3.4/3.5:</b> peralte específico por curva según Radio adoptado (Método 5 AASHTO).</li>");
            sb.AppendLine("<li><b>Numeral 3.3 y Tabla 3.7:</b> variación de la aceleración centrífuga (J), usada en el criterio dinámico del parámetro A de la espiral.</li>");
            sb.AppendLine("<li><b>Numeral 5.4.1.1 y Tabla 5.5:</b> sobreancho en curvas para vehículo rígido.</li>");
            sb.AppendLine("<li><b>Numeral 5.4.1.2 y Tablas 5.6/5.7:</b> sobreancho en curvas para vehículo articulado 3S2 (metodología AASHTO 2004).</li>");
            sb.AppendLine("<li><b>Numeral 3.2.2 y Tabla 3.6:</b> longitud de transición del peralte y del sobreancho.</li>");
            sb.AppendLine("<li><b>Numeral 4.1.2 y Tabla 4.2:</b> pendiente longitudinal máxima.</li>");
            sb.AppendLine("<li><b>Numeral 4.1.3 y Tabla 4.3:</b> longitud mínima de la tangente vertical.</li>");
            sb.AppendLine("<li><b>Numeral 4.2.3 y Tabla 4.4:</b> parámetro K mínimo, distancia de visibilidad de parada y longitud mínima por criterio de operación en curvas verticales.</li>");
            sb.AppendLine("<li><b>Numeral 5.2 y Tabla 5.1:</b> ancho de zona o derecho de vía.</li>");
            sb.AppendLine("<li><b>Numeral 5.3.1.1 y Tabla 5.2:</b> ancho de calzada.</li>");
            sb.AppendLine("<li><b>Numeral 5.3.1.2 y Tabla 5.3:</b> bombeo de la calzada.</li>");
            sb.AppendLine("<li><b>Numeral 5.3.2.1 y Tabla 5.4:</b> ancho de bermas.</li>");
            sb.AppendLine("</ul>");

            sb.AppendLine("<h2>2. LOCALIZACIÓN DEL PROYECTO</h2>");
            sb.AppendLine($"<p>El proyecto en referencia se encuentra ubicado en el eje denominado <b>{nombreEje}</b>.</p>");

            sb.AppendLine("<h2>3. CONSIDERACIONES GENERALES Y CRITERIOS DE DISEÑO</h2>");
            sb.AppendLine("<h3>3.1. Clasificación de la Carretera.</h3>");
            sb.AppendLine("<h4>3.1.1. Según su Funcionalidad.</h4>");
            sb.AppendLine($"<p>Dando cumplimiento al MDG INVIAS 2008 en su numeral 1.2.1 se clasifica el tramo como una carretera <b>{catVia}</b>.</p>");
            sb.AppendLine("<h4>3.1.2. Según Tipo de Terreno.</h4>");
            sb.AppendLine($"<p>El tramo objeto de estudio se considera como un <b>Terreno {terreno}</b> según lo establecido en el MDG INVIAS 2008 en su numeral 1.2.2.1.</p>");

            sb.AppendLine("<h3>3.2. Velocidad de Diseño</h3>");
            sb.AppendLine("<p>En el proceso de asignación de la Velocidad de Diseño se debe otorgar la máxima prioridad a la seguridad de los usuarios. Para garantizar la consistencia en la velocidad, se deben identificar a lo largo del corredor de ruta tramos homogéneos (numeral 2.3, Tabla 2.1 MDG INVIAS 2008).</p>");
            sb.AppendLine("<h4>3.2.1. Velocidad de diseño del tramo homogéneo (Vtr)</h4>");
            sb.AppendLine($"<p>Teniendo en cuenta la clasificación de la carretera y el tipo de terreno se escogió una velocidad de diseño de <b>{vtr} km/h</b> según lo establecido en la Tabla 2.1 del MDG Invias 2008. El Manual expresa esta tabla como un rango admisible por celda; el valor mostrado a continuación es el adoptado por el asistente (extremo superior del rango) para la categoría y terreno de este proyecto.</p>");
            sb.AppendLine(ConstruirTablaVelocidadDiseno(catIdx, terIdx));
            sb.AppendLine("<h4>3.2.2. Velocidad específica de la curva horizontal.</h4>");
            sb.AppendLine("<p>Para asignar la Velocidad Específica (VCH) a las curvas horizontales, se consideran los parámetros de deflexión y entretangencia según la Tabla 2.2 del MDG INVIAS 2008. Por simplificación de anteproyecto, en este modelo se adopta VCH = Vtr para todas las curvas del tramo; ver numeral 8 (Limitaciones y alcance) para el efecto de esta hipótesis.</p>");

            sb.AppendLine("<h3>3.3. Derecho de Vía</h3>");
            sb.AppendLine("<p>Es la faja de terreno destinada a la construcción, mantenimiento y futuras ampliaciones. Según la Tabla 5.1 del MDG INVIAS 2008, el ancho de zona o derecho de vía recomendado para la categoría de esta carretera se indica en la fila resaltada de la tabla siguiente; por tratarse de un rango, el valor final debe fijarse dentro de él según las condiciones particulares del corredor (topografía, predios, obras de drenaje).</p>");
            sb.AppendLine(ConstruirTablaDerechoVia(catIdx));

            sb.AppendLine("<h3>3.4. Ancho de Calzada.</h3>");
            sb.AppendLine($"<p>La Tabla 5.2 del MDG INVIAS 2008 indica el ancho de calzada recomendado en función de la categoría de la carretera, del tipo de terreno y de la velocidad de diseño; la celda resaltada corresponde a la combinación de este proyecto. El asistente adopta, para efectos de la geometría transversal generada en Civil 3D, un ancho de calzada de <b>{anchoCarril * 2:F2} metros</b> (carril de {anchoCarril:F2} m), el cual debe verificarse frente al valor tabulado del Manual para esta categoría, terreno y velocidad de diseño.</p>");
            sb.AppendLine(ConstruirTablaAnchoCalzada(catIdx, terIdx, vtr));

            sb.AppendLine("<h3>3.5. Pendiente transversal en entretangencias horizontales.</h3>");
            sb.AppendLine("<p>El MDG INVIAS 2008 en su numeral 5.3.1.2 indica que en entretangencias horizontales las calzadas deben tener una inclinación transversal denominada bombeo (Tabla 5.3). Se adopta un bombeo del 2%, propio de superficies de rodadura en concreto hidráulico o asfáltico.</p>");
            sb.AppendLine(ConstruirTablaBombeo());

            sb.AppendLine("<h3>3.6. Ancho de Berma.</h3>");
            sb.AppendLine("<p>El ancho de berma depende de la categoría de la carretera, el tipo de terreno y la velocidad de diseño (Vtr), según la Tabla 5.4 del manual; la celda resaltada corresponde a la combinación de este proyecto.</p>");
            sb.AppendLine(ConstruirTablaAnchoBermas(catIdx, terIdx, vtr));

            sb.AppendLine("<h3>3.7. Vehículo de diseño.</h3>");
            sb.AppendLine($"<p>Según la funcionalidad del proyecto y de acuerdo con el numeral 2.2 del MDG INVIAS 2008, se escoge como vehículo de diseño el <b>{vDiseno}</b> (L = {vehiculo} m, dimensión usada para verificaciones de giro). Para el cálculo específico del sobreancho en curvas (numeral 5) se emplea, en cambio, el parámetro L de la Tabla 5.5 del Manual, correspondiente a la distancia entre el parachoques delantero y el eje trasero del vehículo representativo.</p>");
            sb.AppendLine("<p>Las dimensiones de carrocería (longitud, ancho, distancia entre ejes, volados y ángulo máximo de dirección) de los vehículos de diseño tipo del Manual, con la fila del vehículo adoptado resaltada, se reproducen a continuación:</p>");
            sb.AppendLine(ConstruirTablaVehiculosDiseno(CmbVehiculo.SelectedIndex));

            sb.AppendLine("<h3>3.8. Peralte Máximo.</h3>");
            sb.AppendLine($"<p>De acuerdo con el numeral 3.1.3.2 del MDG INVIAS 2008, se adopta un peralte máximo <b>e<sub>máx</sub> = {eMaxProyecto:F0}%</b> para el proyecto ({(catIdx == 3 ? "carreteras Terciarias" : "carreteras Primarias y Secundarias")}). Este es el techo normativo, no el valor forzado en toda curva: el peralte específico de cada curva se asigna según su Radio adoptado, interpolando en las Tablas 3.4/3.5 (numeral 3.1.3.5, Método 5 AASHTO) — ver numeral 5.1.1.2 para el detalle curva por curva.</p>");

            sb.AppendLine("<h2>4. TRAZADO PROPUESTO.</h2>");
            sb.AppendLine("<h3>4.1. Disposición Geométrica del Trazado.</h3>");
            sb.AppendLine("<p>El trazado propuesto está conformado por un eje continuo que obedece a las determinantes topográficas y parámetros de diseño expuestos anteriormente, siguiendo los lineamientos del Manual de diseño Geométrico de Invias 2008.</p>");

            sb.AppendLine("<h2>5. ALINEAMIENTO HORIZONTAL</h2>");
            sb.AppendLine("<h3>5.1. Elementos de Alineamiento horizontal</h3>");
            sb.AppendLine("<p>A continuación, se relacionan los elementos geométricos que componen el eje proyectado:</p>");

            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th colspan='4'>Tabla 10. Elementos de alineamiento Horizontal</th></tr>");
            sb.AppendLine("<tr><th>N° / Tipo</th><th>Longitud</th><th>P.K. inicial</th><th>P.K. final</th></tr>");
            foreach (var el in alignElements) {
                sb.AppendLine($"<tr><td>{el.Type} {el.Elem}</td><td>{el.Length:F2} m</td><td>{FormatearAbscisa(el.StartSt)}</td><td>{FormatearAbscisa(el.EndSt)}</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<h4>5.1.1. Justificación de criterios de diseño para Alineamiento Horizontal.</h4>");
            sb.AppendLine("<h5>5.1.1.1. Radio mínimo de curvatura (RCmín)</h5>");
            sb.AppendLine("<p>De acuerdo con el numeral 3.1.3.4 del MDG Invias 2008, el radio mínimo se obtiene de la ecuación de equilibrio ante el deslizamiento, en función del peralte máximo del proyecto (e<sub>máx</sub>) y del coeficiente de fricción transversal máxima (f<sub>Tmáx</sub>) de la Tabla 3.1:</p>");
            sb.AppendLine("<div class='formula-box'>R<sub>Cmín</sub> = V<sub>CH</sub>² / [127 × (e<sub>máx</sub> + f<sub>Tmáx</sub>)]</div>");
            sb.AppendLine($"<p>Para V<sub>CH</sub> = {vtr} km/h: f<sub>Tmáx</sub> = {f_max:F2} (Tabla 3.1) y e<sub>máx</sub> = {eMaxProyecto:F0}% (numeral 3.1.3.2), de donde R<sub>Cmín</sub> = <b>{r_min_calc:F0} m</b> (valor calculado redondeado al entero superior, por tratarse de un mínimo; puede diferir en hasta 1 m del valor de la Tabla 3.2/3.3 del Manual, que redondea al entero más cercano). La Tabla 3.1 completa del Manual, con la fila usada resaltada, se reproduce a continuación:</p>");
            sb.AppendLine(ConstruirTablaFriccionTransversal(vtr));
            sb.AppendLine($"<p>La Tabla 3.2/3.3 original del Manual, con el valor de R<sub>Cmín</sub> tabulado para V<sub>CH</sub> = {vtr} km/h resaltado, se reproduce a continuación:</p>");
            sb.AppendLine(ConstruirTablaRadioMinimo(vtr, catIdx));

            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th colspan='6'>Tabla 12. Verificación de Radios mínimos (Tablas 3.1 a 3.3 MDG INVIAS 2008)</th></tr>");
            sb.AppendLine("<tr><th>ID</th><th>Radio de Curva Rc (m)</th><th>Velocidad Específica (km/h)</th><th>Peralte asignado (%)</th><th>Radio Mín. RCmín (m)</th><th>¿Cumple?</th></tr>");
            foreach (var c in curves) {
                string cumple = c.Radius >= c.Rmin ? "Si Cumple" : "No Cumple";
                sb.AppendLine($"<tr><td>{c.Elem}</td><td>{c.Radius:F2}</td><td>{c.Vch}</td><td>{c.E_max:F1}%</td><td>{c.Rmin:F1}</td><td>{cumple}</td></tr>");
            }
            sb.AppendLine("</table>");
            {
                int nIncumple = curves.Count(c => c.Radius < c.Rmin);
                double margenMin = curves.Count > 0 ? curves.Min(c => c.Radius - c.Rmin) : 0;
                sb.AppendLine(nIncumple == 0
                    ? $"<p>Las {curves.Count} curvas horizontales del trazado tienen Radio igual o mayor al radio mínimo normativo; el margen más ajustado corresponde a la curva con menor holgura frente a R<sub>Cmín</sub> ({margenMin:F1} m por encima del mínimo), la cual debe priorizarse en la revisión de campo por ser la más exigida.</p>"
                    : $"<p><b>{nIncumple} de {curves.Count}</b> curvas horizontales tienen Radio inferior al mínimo normativo y no cumplen el criterio de seguridad ante deslizamiento del numeral 3.1.3.4; estas curvas deben rediseñarse aumentando su Radio o, si el corredor no lo permite, revisarse como caso extremo justificado (numeral 3.1.3.4, párrafo 2).</p>");
            }

            sb.AppendLine("<h5>5.1.1.2. Peralte específico por curva</h5>");
            sb.AppendLine("<p>Numeral 3.1.3.5 del MDG INVIAS 2008 (Método 5 AASHTO): a cada curva se le asigna el peralte requerido por su Radio adoptado, interpolando en la Tabla 3.4 (Primarias/Secundarias, e<sub>máx</sub>=8%) o 3.5 (Terciarias, e<sub>máx</sub>=6%). Solo las curvas con Radio igual o cercano a R<sub>Cmín</sub> reciben e<sub>máx</sub>; curvas con Radio holgado reciben un peralte menor, y por tanto una transición de peralte (Lt) más corta que si se forzara e<sub>máx</sub> en todo el trazado.</p>");
            {
                double radioMasCritico = curves.Count > 0 ? curves.Min(c => c.Radius) : 0;
                sb.AppendLine($"<p>La Tabla {(catIdx == 3 ? "3.5" : "3.4")} completa del Manual se reproduce a continuación, con la celda correspondiente a la curva de menor Radio del trazado ({radioMasCritico:F0} m, la más exigente) resaltada:</p>");
                sb.AppendLine(ConstruirTablaPeraltePorRadio(catIdx, vtr, radioMasCritico));
            }
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th colspan='4'>Tabla 11. Peralte asignado por curva (Tablas 3.4/3.5 MDG INVIAS 2008)</th></tr>");
            sb.AppendLine("<tr><th>ID</th><th>Radio Rc (m)</th><th>Peralte asignado e (%)</th><th>Peralte máximo del proyecto e<sub>máx</sub> (%)</th></tr>");
            foreach (var c in curves) {
                sb.AppendLine($"<tr><td>{c.Elem}</td><td>{c.Radius:F2}</td><td>{c.E_max:F2}%</td><td>{eMaxProyecto:F1}%</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<h5>5.1.1.3. Sobreancho en las curvas</h5>");
            {
                string numeralAplicable = esArticulado3S2 ? "5.4.1.2" : "5.4.1.1";
                int nSobreancho = curves.Count(c => c.RequiereSobreancho);

                if (nSobreancho == 0)
                {
                    // Ninguna curva del trazado real requiere sobreancho: se documenta el
                    // criterio de aplicabilidad verificado y su resultado, sin desplegar la
                    // fórmula ni las Tablas 5.5/5.6/5.7, que no son pertinentes a este diseño.
                    sb.AppendLine($"<p>El numeral {numeralAplicable} del MDG INVIAS 2008 exige sobreancho únicamente en curvas de Radio menor a 160 m (o en calzadas de más de 7.0 m en tangente con deflexión mayor a 120°). Se verificó este criterio en las <b>{curves.Count}</b> curvas horizontales del trazado: ninguna tiene Radio inferior a 160 m, por lo que <b>no se requiere sobreancho en ningún punto del proyecto</b> y la calzada conserva su ancho constante (numeral 3.4) en toda la longitud del tramo. Por no ser aplicable a este diseño, no se reproducen aquí la fórmula ni las tablas de dimensiones del vehículo (Tabla {(esArticulado3S2 ? "5.6/5.7" : "5.5")}); ver numeral {numeralAplicable} del Manual si se requieren para otro tramo del proyecto.</p>");
                }
                else if (!esArticulado3S2)
                {
                    sb.AppendLine("<p>Numeral 5.4.1.1 del MDG INVIAS 2008 (vehículos rígidos, Figura 5.3 y Tabla 5.5). El sobreancho por carril se obtiene de la geometría del vehículo tipo recorriendo la curva (Figura 5.3), y para una calzada de n carriles se generaliza a:</p>");
                    sb.AppendLine("<div class='formula-box'>S = n × (R<sub>C</sub> − √(R<sub>C</sub>² − L²))</div>");
                    sb.AppendLine("<p>donde L es la distancia entre el parachoques delantero y el eje trasero del vehículo representativo (Tabla 5.5, reproducida abajo con la categoría adoptada resaltada), y n = 2 carriles para la calzada bidireccional de este proyecto. El sobreancho se limita, según el mismo numeral, a curvas de Radio menor a 160 m, salvo que la calzada en tangente supere 7.0 m con deflexión mayor a 120°, caso en el que igualmente se requiere sobreancho.</p>");
                    sb.AppendLine(ConstruirTablaSobreanchoVehiculos(ObtenerLongitudVehiculoSobreancho()));
                }
                else
                {
                    sb.AppendLine("<p>El vehículo de diseño adoptado (T3-S2) es un vehículo <b>articulado</b>, por lo que la fórmula de cuerpo rígido del numeral 5.4.1.1 no es aplicable (un tractocamión con semirremolque no se desplaza como un sólido único al tomar la curva). El numeral 5.4.1.2 del MDG INVIAS 2008 adopta para este caso la metodología del Manual AASHTO versión 2004:</p>");
                    sb.AppendLine("<div class='formula-box'>S = A<sub>C</sub> − A<sub>T</sub></div>");
                    sb.AppendLine("<div class='formula-box'>A<sub>C</sub> = n × (U + C) + (n − 1) × F<sub>A</sub> + Z</div>");
                    sb.AppendLine("<p>donde A<sub>T</sub> es el ancho de calzada en tangente, n el número de carriles, U el ancho ocupado por el vehículo al describir la trayectoria en curva, C el espacio lateral de seguridad (Tabla 5.7), F<sub>A</sub> el avance del voladizo delantero sobre el carril adyacente y Z un sobreancho adicional de seguridad, experimental, función de V<sub>CH</sub> y R<sub>C</sub>:</p>");
                    sb.AppendLine("<div class='formula-box'>U = u + R<sub>C</sub> − √(R<sub>C</sub>² − (L<sub>1</sub>+L<sub>2</sub>+L<sub>3</sub>)²)</div>");
                    sb.AppendLine("<div class='formula-box'>F<sub>A</sub> = √(R<sub>C</sub>² + A × (2×L<sub>1</sub> + A)) − R<sub>C</sub></div>");
                    sb.AppendLine("<div class='formula-box'>Z = 0.1 × √(V<sub>CH</sub> / R<sub>C</sub>)</div>");
                    sb.AppendLine("<p>Las dimensiones u, A, L<sub>1</sub>, L<sub>2</sub> y L<sub>3</sub> del vehículo articulado 3S2 (Tabla 5.6) y el valor de C interpolado de la Tabla 5.7 para el ancho de calzada en tangente de este proyecto se muestran a continuación:</p>");
                    sb.AppendLine(ConstruirTablaVehiculoArticulado());
                    sb.AppendLine(ConstruirTablaEspacioLateralSeguridad(anchoCarril * 2.0));
                    sb.AppendLine("<p>El sobreancho se limita, igual que para vehículos rígidos, a curvas de Radio menor a 160 m, salvo que la calzada en tangente supere 7.0 m con deflexión mayor a 120°, caso en el que igualmente se requiere sobreancho.</p>");
                }

                if (nSobreancho > 0)
                {
                    sb.AppendLine("<table>");
                    sb.AppendLine("<tr><th colspan='6'>Tabla 13. Sobreancho requerido por curva</th></tr>");
                    sb.AppendLine("<tr><th>ID</th><th>Radio Rc (m)</th><th>Δ (°)</th><th>¿Requiere sobreancho? (R&lt;160 m)</th><th>Sobreancho S (m)</th><th>Ancho de calzada final (m)</th></tr>");
                    foreach (var c in curves) {
                        string requiere = c.RequiereSobreancho ? "Sí" : "No";
                        sb.AppendLine($"<tr><td>{c.Elem}</td><td>{c.Radius:F2}</td><td>{c.Delta:F2}</td><td>{requiere}</td><td>{c.S_max:F2}</td><td>{(anchoCarril * 2 + c.S_max):F2}</td></tr>");
                    }
                    sb.AppendLine("</table>");
                    sb.AppendLine($"<p><b>{nSobreancho} de {curves.Count}</b> curvas requieren sobreancho por tener Radio menor a 160 m. El sobreancho máximo del tramo es de {curves.Where(c => c.RequiereSobreancho).Select(c => c.S_max).DefaultIfEmpty(0).Max():F2} m, correspondiente a la curva de menor Radio; todo este ensanche debe construirse hacia el interior de la curva, conforme lo indica el numeral {numeralAplicable}.</p>");
                }
            }

            sb.AppendLine("<h5>5.1.1.4. Longitud de transición del peralte y del sobreancho</h5>");
            sb.AppendLine("<p>El numeral 3.2.2 del MDG Invias 2008 distingue dos casos. En <b>curvas circulares simples</b> (sin espiral), la longitud de transición se obtiene de la pendiente relativa máxima de la rampa de peraltes Δs (Tabla 3.6, reproducida abajo), con peralte inicial e<sub>i</sub> = 0 y peralte final e<sub>f</sub> igual al peralte máximo del proyecto:</p>");
            sb.AppendLine("<div class='formula-box'>L = a × (e<sub>f</sub> − e<sub>i</sub>) / Δs</div>");
            sb.AppendLine($"<p>Para V<sub>CH</sub> = {vtr} km/h, Δs<sub>máx</sub> = {deltaSMax:F2}%. Esta es la ÚNICA expresión normativa para este caso: no existe en el Manual un segundo término dependiente de la velocidad que deba sumarse o compararse; usar exclusivamente esta fórmula evita transiciones sobredimensionadas.</p>");
            sb.AppendLine("<p>En <b>curvas con espiral de transición</b> (numeral 3.2.2.2), la transición del peralte —y, según el numeral 5.4.2, también la del sobreancho— se desarrolla linealmente en la longitud REAL de la espiral (Le) ya trazada en el eje, no en el valor teórico anterior; por eso, cuando el eje analizado incluye espirales, la columna \"Origen de Lt\" de la tabla siguiente indica Le en vez de la fórmula de Δs.</p>");
            sb.AppendLine(ConstruirTablaDeltaSMaximo(vtr));
            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th colspan='8'>Tabla 21. Longitud de transición aplicada</th></tr>");
            sb.AppendLine("<tr><th>ID</th><th>Radio [Rc]</th><th>Origen de Lt</th><th>Le entrada (m)</th><th>Le salida (m)</th><th>Longitud Transición [Lt] (m)</th><th>Δs Máx. Normativo (%)</th><th>¿Cumple?</th></tr>");
            foreach (var c in curves) {
                string cumple = c.AsCalc <= c.AsMax + 0.001 ? "Si Cumple" : "No Cumple";
                string origen = c.TieneEspiral ? "Espiral real (Le)" : "Tabla 3.6 (Δs)";
                sb.AppendLine($"<tr><td>{c.Elem}</td><td>{c.Radius:F2}</td><td>{origen}</td><td>{(c.TieneEspiral ? c.LeEntrada.ToString("F2") : "-")}</td><td>{(c.TieneEspiral ? c.LeSalida.ToString("F2") : "-")}</td><td>{c.Lt:F2}</td><td>{c.AsMax:F2}%</td><td>{cumple}</td></tr>");
            }
            sb.AppendLine("</table>");
            int nConEspiral = curves.Count(c => c.TieneEspiral);
            sb.AppendLine(nConEspiral == 0
                ? $"<p>Ninguna curva del eje analizado tiene espiral de transición asociada; al aplicarse la Tabla 3.6 de forma directa, la longitud de transición Lt = {(curves.Count > 0 ? curves[0].Lt : 0):F2} m es igual para todas las curvas del tramo, pues depende únicamente de V<sub>CH</sub> (constante en este anteproyecto) y del peralte máximo del proyecto, no del Radio individual de cada curva.</p>"
                : $"<p><b>{nConEspiral} de {curves.Count}</b> curvas del eje analizado están espiralizadas; para éstas, Lt corresponde a la longitud real de la espiral dibujada (se toma la mayor entre entrada y salida cuando difieren) y no al valor teórico de la Tabla 3.6.</p>");

            if (nConEspiral > 0)
            {
                sb.AppendLine("<h5>5.1.1.5. Parámetro A de la espiral</h5>");
                sb.AppendLine("<p>El numeral 3.3.1 del MDG INVIAS 2008 exige que el parámetro A adoptado (A = √(Rc·Le)) sea igual o mayor a la envolvente superior de tres criterios, y no supere el máximo del numeral 3.3.2:</p>");
                sb.AppendLine("<div class='formula-box'>Criterio I (dinámico): A<sub>mín</sub> = √{ [V<sub>CH</sub>·R<sub>C</sub> / (46.656·J)] · [V<sub>CH</sub>²/R<sub>C</sub> − 1.27·e] }</div>");
                sb.AppendLine("<div class='formula-box'>Criterio II (transición del peralte): A<sub>mín</sub> = √(R<sub>C</sub> · e·a / Δs)</div>");
                sb.AppendLine("<div class='formula-box'>Criterio III (percepción/estética): A<sub>mín</sub> = máx[ ⁴√(6·R<sub>C</sub>³) ; 0.3236·R<sub>C</sub> ]</div>");
                sb.AppendLine("<div class='formula-box'>A<sub>máx</sub> = 1.1 × R<sub>C</sub>  (numeral 3.3.2)</div>");
                sb.AppendLine($"<p>Con J = {ObtenerVariacionAceleracionCentrifuga(vtr):F1} m/s³ (Tabla 3.7, para V<sub>CH</sub> = {vtr} km/h), e = e<sub>máx</sub> del proyecto y a = ancho de carril = {anchoCarril:F2} m (distancia del eje de giro al borde de calzada). El parámetro adoptado se calcula con la longitud de espiral (Le) realmente dibujada en el eje.</p>");
                sb.AppendLine(ConstruirTablaAceleracionCentrifuga(vtr));
                sb.AppendLine("<table>");
                sb.AppendLine("<tr><th colspan='8'>Tabla 14. Verificación del parámetro A de las espirales (numeral 3.3 MDG INVIAS 2008)</th></tr>");
                sb.AppendLine("<tr><th>ID</th><th>Radio Rc (m)</th><th>A entrada (m)</th><th>A salida (m)</th><th>A mínimo normativo (m)</th><th>A máximo normativo (m)</th><th>¿Cumple mínimo?</th><th>¿Cumple máximo?</th></tr>");
                foreach (var c in curves.Where(c => c.TieneEspiral))
                {
                    string cumpleMin = (c.AEntrada <= 0 || c.AEntrada >= c.AMinNormativo - 0.01) && (c.ASalida <= 0 || c.ASalida >= c.AMinNormativo - 0.01) ? "Si Cumple" : "No Cumple";
                    string cumpleMax = (c.AEntrada <= 0 || c.AEntrada <= c.AMaxNormativo + 0.01) && (c.ASalida <= 0 || c.ASalida <= c.AMaxNormativo + 0.01) ? "Si Cumple" : "No Cumple";
                    sb.AppendLine($"<tr><td>{c.Elem}</td><td>{c.Radius:F2}</td><td>{(c.AEntrada > 0 ? c.AEntrada.ToString("F2") : "-")}</td><td>{(c.ASalida > 0 ? c.ASalida.ToString("F2") : "-")}</td><td>{c.AMinNormativo:F2}</td><td>{c.AMaxNormativo:F2}</td><td>{cumpleMin}</td><td>{cumpleMax}</td></tr>");
                }
                sb.AppendLine("</table>");
                int nNoCumpleA = curves.Count(c => c.TieneEspiral && (
                    (c.AEntrada > 0 && (c.AEntrada < c.AMinNormativo - 0.01 || c.AEntrada > c.AMaxNormativo + 0.01)) ||
                    (c.ASalida > 0 && (c.ASalida < c.AMinNormativo - 0.01 || c.ASalida > c.AMaxNormativo + 0.01))));
                sb.AppendLine(nNoCumpleA == 0
                    ? "<p>Todas las espirales del eje analizado tienen un parámetro A dentro del rango normativo.</p>"
                    : $"<p><b>{nNoCumpleA}</b> espiral(es) tienen un parámetro A fuera del rango normativo (numeral 3.3); redimensione la espiral (ajustando Le o el Radio adoptado) antes de avanzar a diseño definitivo.</p>");
            }

            sb.AppendLine("<h2>6. ALINEAMIENTO VERTICAL</h2>");
            sb.AppendLine("<h3>6.1. Pendientes mínima y máxima de las tangentes verticales.</h3>");
            sb.AppendLine("<p>El numeral 4.1.1 del Manual fija una pendiente longitudinal mínima de 0.5% (deseable) o 0.3% (terreno plano) para garantizar el drenaje de la calzada; esta condición debe verificarse en campo/hidráulicamente sobre la rasante final. Según la Tabla 4.2 (numeral 4.1.2), la pendiente longitudinal máxima permitida para esta vía y velocidad específica es del <b>{pMax}%</b>. La Tabla 4.2 completa del Manual, con la celda de esta categoría y velocidad resaltada, se reproduce a continuación:</p>");
            sb.AppendLine(ConstruirTablaPendienteMaxima(vtr, catIdx));

            sb.AppendLine("<h3>6.2. Longitud Mínima de las tangentes verticales</h3>");
            sb.AppendLine($"<p>Según la Tabla 4.3 (numeral 4.1.3), la longitud mínima de la tangente vertical, medida como proyección horizontal entre PIV y PIV, es de <b>{lMinTangenteVert:F0} m</b> para V<sub>TV</sub> = {vtr} km/h. Este valor gobierna el espaciamiento mínimo entre vértices adoptado en el trazado de la rasante: los PIV del anteproyecto se ubicaron precisamente a este intervalo, de modo que cada tangente vertical resultante cumple el mínimo normativo por construcción.</p>");
            sb.AppendLine(ConstruirTablaLongitudMinimaTangente(vtr));

            sb.AppendLine("<h3>6.3. Curvas Verticales.</h3>");
            sb.AppendLine("<p>Las curvas verticales enlazan dos tangentes consecutivas del alineamiento vertical (numeral 4.2). Al punto de intersección de dos tangentes consecutivas se le designa como PIV, y a la diferencia algebraica de pendientes se le representa por la letra A. Se emplea la parábola cuadrática simétrica (numeral 4.2.2.1).</p>");

            sb.AppendLine("<h4>6.3.1. Determinación de la longitud de la curva vertical.</h4>");
            sb.AppendLine("<p>El numeral 4.2.3 establece tres criterios concurrentes: (i) <i>seguridad</i>, que exige distancia de visibilidad de parada (DP) en toda la curva; (ii) <i>operación</i>, que evita la sensación de cambio súbito de pendiente y fija una longitud mínima; y (iii) <i>drenaje</i>, que limita la longitud máxima (K ≤ 50) para que el sector central de la curva no quede demasiado plano. El primero se controla mediante el parámetro K = L/A de la Tabla 4.4:</p>");
            sb.AppendLine("<div class='formula-box'>L<sub>mín</sub> = K<sub>mín</sub> × A ; con L<sub>mín</sub> ≥ Longitud según criterio de operación</div>");
            sb.AppendLine($"<p>Para V<sub>CV</sub> = {vtr} km/h: K<sub>mín</sub> convexa = <b>{kConvexaUsado:F0}</b>, K<sub>mín</sub> cóncava = <b>{kConcavaUsado:F0}</b>, distancia de visibilidad de parada asociada D<sub>P</sub> = {dpTabla:F0} m, y longitud mínima por criterio de operación = <b>{l_min_vertical:F0} m</b>. Obsérvese que el K mínimo cóncavo es siempre mayor que el convexo a igual velocidad: de noche, la visibilidad en una curva cóncava está limitada por el alcance de las luces delanteras (numeral 4.2.3.2), una restricción más severa que la línea de vista diurna que gobierna las curvas convexas. La Tabla 4.4 completa del Manual se reproduce a continuación:</p>");
            sb.AppendLine(ConstruirTablaKMinimo(vtr));

            sb.AppendLine("<table>");
            sb.AppendLine("<tr><th colspan='12'>Tabla 30. Valores de Parámetro K y Longitud de Curva (Tabla 4.4 MDG INVIAS 2008)</th></tr>");
            sb.AppendLine("<tr><th>Vértice PIV</th><th>Abscisa PIV</th><th>Cota PIV (m)</th><th>Pte. Entrada p1 (%)</th><th>Pte. Salida p2 (%)</th><th>Dif. Algebraica A (%)</th><th>Tipo de Curva</th><th>Parámetro K</th><th>Longitud Lv (m)</th><th>K Mínimo Normativo</th><th>¿Cumple K?</th><th>Drenaje (K≤50)</th></tr>");
            if (pvis.Count == 0) {
                sb.AppendLine("<tr><td colspan='12'><i>No se detectaron curvas verticales en el perfil.</i></td></tr>");
            } else {
                foreach (var pvi in pvis) {
                    string cumple = "N/A";
                    string kDis = "-";
                    string lDis = "Recto";
                    string drenaje = "N/A";

                    if(pvi.CurveLength > 0.01) {
                        cumple = pvi.K >= pvi.KminNorma ? "Si Cumple" : "No Cumple";
                        kDis = pvi.K.ToString("F2");
                        lDis = pvi.CurveLength.ToString("F2");
                        drenaje = pvi.K <= 50.0 ? "Cumple" : "Revisar drenaje";
                    }
                    else if(pvi.A >= 0.5) {
                        cumple = "No Cumple (Falta Curva)";
                    }
                    else if(pvi.A < 0.5 && pvi.GradeIn != 0 && pvi.GradeOut != 0) {
                        cumple = "Recto (A<0.5%)";
                    }

                    sb.AppendLine($"<tr><td>PIV-{pvi.Id}</td><td>{FormatearAbscisa(pvi.Station)}</td><td>{pvi.Elevation:F2}</td><td>{pvi.GradeIn:F2}</td><td>{pvi.GradeOut:F2}</td><td>{pvi.A:F2}</td><td>{pvi.Type}</td><td>{kDis}</td><td>{lDis}</td><td>{pvi.KminNorma}</td><td>{cumple}</td><td>{drenaje}</td></tr>");
                }
            }
            sb.AppendLine("</table>");
            sb.AppendLine("<p class='cita'>La columna \"Drenaje (K≤50)\" corresponde al criterio del numeral 4.2.3.1/4.2.3.2: cuando K supera 50, el sector central de la curva puede resultar demasiado plano y se recomienda prever sumideros o pendiente transversal adicional en ese punto.</p>");
            {
                int nConCurva = pvis.Count(p => p.CurveLength > 0.01);
                int nFaltaCurva = pvis.Count(p => p.CurveLength <= 0.01 && p.A >= 0.5);
                int nRecto = pvis.Count(p => p.A < 0.5);
                int nDrenajeRevisar = pvis.Count(p => p.CurveLength > 0.01 && p.K > 50.0);
                sb.AppendLine($"<p>De los {pvis.Count} PIV internos del perfil, {nConCurva} recibieron curva vertical, {nRecto} no la requerían por tener una diferencia algebraica de pendientes A menor a 0.5% (umbral de percepción del numeral 4.2), y {nFaltaCurva} presentaron A ≥ 0.5% sin que se haya podido confirmar una curva construida en el modelo — estos últimos deben revisarse manualmente en el editor de rasante de Civil 3D, ajustando la geometría de la tangente vertical adyacente si el espacio disponible entre PIV es insuficiente para alojar la longitud mínima normativa.{(nDrenajeRevisar > 0 ? $" Adicionalmente, {nDrenajeRevisar} curva(s) superan K=50 y deben revisarse por el criterio de drenaje del numeral 4.2.3.1/4.2.3.2." : "")}</p>");
            }

            sb.AppendLine("<h3>6.4. Verificación de las tangentes verticales resultantes</h3>");
            sb.AppendLine($"<p>A diferencia del numeral 6.2 (que verifica el espaciamiento ADOPTADO entre PIV), esta verificación mide la longitud REAL de recta que queda entre el PTV de una curva vertical y el PCV de la siguiente, una vez restado el desarrollo de ambas curvas — la magnitud que gobierna realmente si un conductor percibe un cambio de pendiente aislado o continuo. Se compara contra la longitud mínima de la Tabla 4.3 ({lMinTangenteVert:F0} m para V<sub>TV</sub> = {vtr} km/h).</p>");
            if (pvis.Count < 2)
            {
                sb.AppendLine("<p><i>Solo hay un PIV interno (o ninguno); no aplica verificación de tangente entre curvas verticales.</i></p>");
            }
            else
            {
                sb.AppendLine("<table>");
                sb.AppendLine("<tr><th colspan='5'>Tabla 31. Tangentes verticales resultantes entre curvas consecutivas</th></tr>");
                sb.AppendLine("<tr><th>Entre</th><th>PVT curva anterior</th><th>PVC curva siguiente</th><th>Longitud de tangente resultante (m)</th><th>¿Cumple L mín. Tabla 4.3?</th></tr>");
                int nTangentesCortas = 0;
                for (int i = 0; i < pvis.Count - 1; i++)
                {
                    var pviA = pvis[i]; var pviB = pvis[i + 1];
                    double pvt = pviA.Station + pviA.CurveLength / 2.0;
                    double pvc = pviB.Station - pviB.CurveLength / 2.0;
                    double tangenteResultante = pvc - pvt;
                    bool cumpleTangente = tangenteResultante >= lMinTangenteVert - 0.01;
                    if (!cumpleTangente) nTangentesCortas++;
                    sb.AppendLine($"<tr><td>PIV-{pviA.Id} → PIV-{pviB.Id}</td><td>{FormatearAbscisa(pvt)}</td><td>{FormatearAbscisa(pvc)}</td><td>{tangenteResultante:F2}</td><td>{(cumpleTangente ? "Si Cumple" : "No Cumple")}</td></tr>");
                }
                sb.AppendLine("</table>");
                sb.AppendLine(nTangentesCortas == 0
                    ? "<p>Todas las tangentes verticales resultantes cumplen la longitud mínima de la Tabla 4.3.</p>"
                    : $"<p><b>{nTangentesCortas}</b> tangente(s) vertical(es) resultante(s) quedan por debajo del mínimo normativo una vez descontado el desarrollo de las curvas adyacentes; considere fusionar los PIV involucrados en una sola curva de mayor longitud, o separarlos, antes de diseño definitivo.</p>");
            }

            sb.AppendLine("<h3>6.5. Coordinación entre el alineamiento horizontal y el vertical</h3>");
            sb.AppendLine("<p>El MDG INVIAS 2008 no fija un criterio numérico único de coordinación planta-perfil; la buena práctica generalizada (AASHTO y literatura técnica) recomienda que las curvas verticales, especialmente las cóncavas, queden en lo posible dentro del desarrollo de una curva horizontal, evitando que una curva vertical caiga en medio de una tangente horizontal larga y recta, donde el cambio de pendiente es más difícil de anticipar visualmente (efecto de \"bache oculto\"). Esta sección solo señala la condición geométrica para que el proyectista aplique su criterio; no constituye una verificación normativa de cumplimiento obligatorio.</p>");
            {
                List<string> filasCoord = new List<string>();
                foreach (var pvi in pvis)
                {
                    if (pvi.CurveLength <= 0.01) continue;
                    double pcv = pvi.Station - pvi.CurveLength / 2.0;
                    double ptv = pvi.Station + pvi.CurveLength / 2.0;
                    bool coincideConHorizontal = curves.Any(c => ptv > c.StartSt && pcv < c.EndSt);
                    string obs = coincideConHorizontal
                        ? "Coincide con curva horizontal"
                        : (pvi.Type == "Cóncava" ? "En tangente recta — revisar visibilidad nocturna" : "En tangente recta");
                    filasCoord.Add($"<tr><td>PIV-{pvi.Id}</td><td>{FormatearAbscisa(pvi.Station)}</td><td>{pvi.Type}</td><td>{obs}</td></tr>");
                }
                if (filasCoord.Count == 0)
                {
                    sb.AppendLine("<p><i>No hay curvas verticales construidas para evaluar coordinación con la planta.</i></p>");
                }
                else
                {
                    sb.AppendLine("<table>");
                    sb.AppendLine("<tr><th colspan='4'>Tabla 32. Coordinación planta-perfil (informativa)</th></tr>");
                    sb.AppendLine("<tr><th>PIV</th><th>Abscisa</th><th>Tipo</th><th>Observación</th></tr>");
                    foreach (var fila in filasCoord) sb.AppendLine(fila);
                    sb.AppendLine("</table>");
                }
            }

            sb.AppendLine("<h2>7. DISTANCIA DE VISIBILIDAD DE PARADA</h2>");
            sb.AppendLine("<p>La distancia de visibilidad de parada (DP), tabulada en la Tabla 4.4 en función de la Velocidad Específica, es la base del criterio de seguridad tanto en curvas verticales (numeral 4.2.3.1) como en la verificación de despeje lateral en curvas horizontales (numeral 5.5, valor de la flecha M). Como referencia analítica, la expresión general con h1 = 1.08 m (altura del ojo del conductor) y h2 = 0.60 m (altura del obstáculo) es:</p>");
            sb.AppendLine($"<div class='formula-box'>D<sub>P</sub> calculada ≈ {dp_calc:F0} m (fórmula de frenado AASHTO, t<sub>PR</sub>=2.5 s, f<sub>l</sub>={fl_long:F2}) — D<sub>P</sub> normativa Tabla 4.4 = {dpTabla:F0} m</div>");
            sb.AppendLine("<p>Se adopta para todos los chequeos de la presente memoria el valor tabulado en la Tabla 4.4, por ser el valor oficial de referencia del Manual.</p>");

            sb.AppendLine("<h2>8. LIMITACIONES Y ALCANCE DEL ANTEPROYECTO</h2>");
            sb.AppendLine("<p>Esta memoria documenta un modelo de <b>anteproyecto asistido</b>, generado automáticamente a partir del eje dibujado por el proyectista y la superficie de terreno natural. Antes de avanzar a diseño de detalle, se debe verificar en campo o con el criterio del diseñador lo siguiente:</p>");
            sb.AppendLine("<ul>");
            sb.AppendLine($"<li><b>Peralte por curva:</b> el peralte se asigna curva por curva según el Radio adoptado (numeral 3.1.3.5, Método 5 AASHTO, Tablas 3.4/3.5), no un valor único e<sub>máx</sub> forzado en todo el trazado; e<sub>máx</sub> = {eMaxProyecto:F0}% solo se aplica a las curvas en o cerca de R<sub>Cmín</sub>. La interpolación es lineal entre los renglones tabulados del Manual, práctica estándar de ingeniería para este tipo de tabla.</li>");
            sb.AppendLine("<li><b>Velocidad específica por curva (VCH):</b> se asumió VCH = Vtr en todo el tramo. En proyectos con curvas de radio holgado, la Tabla 2.2 puede asignar velocidades específicas mayores, lo que debe revisarse curva a curva.</li>");
            sb.AppendLine("<li><b>Rasante:</b> los PIV se ubicaron mediante muestreo a intervalos regulares (longitud mínima de tangente vertical, Tabla 4.3) ajustados a la pendiente máxima; esta rasante preliminar debe optimizarse por el proyectista para balancear cortes y terraplenes y para no incumplir la pendiente longitudinal mínima de drenaje (numeral 4.1.1).</li>");
            sb.AppendLine("<li><b>Sobreancho:</b> aplicable únicamente en curvas con Radio &lt; 160 m según el numeral 5.4.1.1; en vías Terciarias puede además verificarse con el método gráfico simplificado de la Figura 5.4 (s = 32/RC por carril).</li>");
            sb.AppendLine("<li><b>Vehículo articulado:</b> para corredores con tránsito significativo de vehículos articulados (tracto-camión), el numeral 5.4.1.2 recomienda el método AASHTO 2004 con el vehículo 3S2 (Tabla 5.6/5.7), más exigente que el método de vehículo rígido aquí empleado.</li>");
            sb.AppendLine("</ul>");

            sb.AppendLine("<h2>9. CONCLUSIONES.</h2>");
            sb.AppendLine("<ul>");
            sb.AppendLine($"<li>Se clasificó la vía como una carretera {catVia}, en terreno {terreno}, con velocidad de diseño Vtr = {vtr} km/h.</li>");
            sb.AppendLine($"<li>El radio mínimo normativo para esta velocidad es RCmín = {r_min_calc:F1} m; {(curves.Count(c => c.Radius < c.Rmin) == 0 ? "todas las curvas horizontales del trazado lo cumplen." : $"{curves.Count(c => c.Radius < c.Rmin)} de {curves.Count} curvas horizontales no lo cumplen y deben corregirse.")}</li>");
            sb.AppendLine($"<li>{curves.Count(c => c.RequiereSobreancho)} de {curves.Count} curvas horizontales requieren sobreancho por tener Radio menor a 160 m.</li>");
            sb.AppendLine($"<li>Se generaron {pvis.Count(p => p.CurveLength > 0.01)} curvas verticales de un total de {pvis.Count} PIV internos; {pvis.Count(p => p.CurveLength <= 0.01 && p.A >= 0.5)} vértices quedaron sin curva por restricciones de espacio y deben revisarse manualmente.</li>");
            sb.AppendLine("<li>Este documento y el modelo generado corresponden a un anteproyecto; los puntos listados en el numeral 8 deben resolverse antes de avanzar a diseño definitivo, estructuración de pavimentos y drenaje.</li>");
            sb.AppendLine("</ul>");

            sb.AppendLine("<h2>10. BIBLIOGRAFÍA</h2>");
            sb.AppendLine("<ul>");
            sb.AppendLine("<li>Aashto. (2011). A Policy on Geometric Design of Highways and Streets.</li>");
            sb.AppendLine("<li>Instituto Nacional de Vías – INVIAS. (2008). Manual de Diseño Geométrico de Carreteras. Capítulos 1 a 5.</li>");
            sb.AppendLine("</ul>");

            sb.AppendLine("<h2>11. ANEXOS</h2>");
            sb.AppendLine("<ul>");
            sb.AppendLine("<li>Anexo 1. Planos Planta Perfil</li>");
            sb.AppendLine("<li>Anexo 2. Planos Secciones Transversales</li>");
            sb.AppendLine("<li>Anexo 3. Carteras de Diseño</li>");
            sb.AppendLine("</ul>");

            sb.AppendLine("</body></html>");

            return sb.ToString();
        }

        // ==========================================
        // 🔹 TABLAS DE REFERENCIA DEL MANUAL INVIAS 2008 (reproducidas íntegras)
        // Se insertan en la memoria junto a los resultados del proyecto, resaltando
        // la fila/columna efectivamente usada, para que el documento sea auditable
        // sin necesidad de tener el Manual abierto en paralelo.
        // ==========================================
        private string ResaltarSiCoincide(double valor, double resaltado) =>
            Math.Abs(valor - resaltado) < 0.001 ? " style='background-color:#ffe9a8; font-weight:bold;'" : "";

        private string ConstruirTablaFriccionTransversal(double vtrResaltado)
        {
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='12'>Tabla 3.1 MDG INVIAS 2008 — Coeficiente de fricción transversal máxima (f<sub>Tmáx</sub>)</th></tr>");
            t.Append("<tr><th>V<sub>CH</sub> (km/h)</th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v:F0}</th>");
            t.AppendLine("</tr><tr><th>f<sub>Tmáx</sub></th>");
            foreach (var v in vs) t.Append($"<td{ResaltarSiCoincide(v, vtrResaltado)}>{CalcularFriccionMaxima(v):F2}</td>");
            t.AppendLine("</tr></table>");
            return t.ToString();
        }

        private string ConstruirTablaDeltaSMaximo(double vtrResaltado)
        {
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='12'>Tabla 3.6 MDG INVIAS 2008 — Pendiente relativa máxima de la rampa de peraltes (Δs máx, %)</th></tr>");
            t.Append("<tr><th>V<sub>CH</sub> (km/h)</th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v:F0}</th>");
            t.AppendLine("</tr><tr><th>Δs máx (%)</th>");
            foreach (var v in vs) t.Append($"<td{ResaltarSiCoincide(v, vtrResaltado)}>{ObtenerDeltaSMaximo(v):F2}</td>");
            t.AppendLine("</tr></table>");
            return t.ToString();
        }

        private string ConstruirTablaLongitudMinimaTangente(double vtrResaltado)
        {
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='12'>Tabla 4.3 MDG INVIAS 2008 — Longitud mínima de la tangente vertical (m)</th></tr>");
            t.Append("<tr><th>V<sub>TV</sub> (km/h)</th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v:F0}</th>");
            t.AppendLine("</tr><tr><th>L mín (m)</th>");
            foreach (var v in vs) t.Append($"<td{ResaltarSiCoincide(v, vtrResaltado)}>{ObtenerLongitudMinimaTangenteVertical(v):F0}</td>");
            t.AppendLine("</tr></table>");
            return t.ToString();
        }

        private string ConstruirTablaKMinimo(double vtrResaltado)
        {
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='12'>Tabla 4.4 MDG INVIAS 2008 — K mínimo, distancia de visibilidad de parada y longitud mínima por operación</th></tr>");
            t.Append("<tr><th>V<sub>CV</sub> (km/h)</th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v:F0}</th>");
            t.AppendLine("</tr><tr><th>D<sub>P</sub> (m)</th>");
            foreach (var v in vs) t.Append($"<td{ResaltarSiCoincide(v, vtrResaltado)}>{ObtenerDistanciaVisibilidadParada(v):F0}</td>");
            t.AppendLine("</tr><tr><th>K mín. convexa</th>");
            foreach (var v in vs) t.Append($"<td{ResaltarSiCoincide(v, vtrResaltado)}>{ObtenerKMinimo(v, true):F0}</td>");
            t.AppendLine("</tr><tr><th>K mín. cóncava</th>");
            foreach (var v in vs) t.Append($"<td{ResaltarSiCoincide(v, vtrResaltado)}>{ObtenerKMinimo(v, false):F0}</td>");
            t.AppendLine("</tr><tr><th>L mín. operación (m)</th>");
            foreach (var v in vs) t.Append($"<td{ResaltarSiCoincide(v, vtrResaltado)}>{ObtenerLongitudMinimaCurvaVertical(v):F0}</td>");
            t.AppendLine("</tr></table>");
            return t.ToString();
        }

        private string ConstruirTablaSobreanchoVehiculos(double lResaltado)
        {
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='2'>Tabla 5.5 MDG INVIAS 2008 — Dimensión L para el cálculo del sobreancho (vehículos rígidos)</th></tr>");
            t.AppendLine("<tr><th>Categoría de vehículo</th><th>L (m)</th></tr>");
            (string, double)[] filas = { ("Vehículo liviano", 3.70), ("Bus mediano", 7.25), ("Bus grande", 9.70), ("Camión de dos ejes", 8.00), ("Camión de tres ejes o dobletroque", 7.80) };
            foreach (var (nombre, l) in filas) t.AppendLine($"<tr><td{ResaltarSiCoincide(l, lResaltado)}>{nombre}</td><td{ResaltarSiCoincide(l, lResaltado)}>{l:F2}</td></tr>");
            t.AppendLine("</table>");
            return t.ToString();
        }

        // Tabla 5.6 INVIAS (numeral 5.4.1.2) - Dimensiones del vehículo articulado 3S2.
        private string ConstruirTablaVehiculoArticulado()
        {
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='6'>Tabla 5.6 MDG INVIAS 2008 — Dimensiones para el cálculo del sobreancho requerido por el vehículo articulado representativo del parque automotor colombiano</th></tr>");
            t.AppendLine("<tr><th>Categoría</th><th>Descripción</th><th>A (m)</th><th>L<sub>1</sub> (m)</th><th>L<sub>2</sub> (m)</th><th>L<sub>3</sub> (m)</th><th>u (m)</th></tr>");
            t.AppendLine($"<tr style='background-color:#ffe9a8; font-weight:bold;'><td>3S2</td><td>Tractocamión de tres ejes con semirremolque de dos ejes</td><td>{VEH_3S2_A:F2}</td><td>{VEH_3S2_L1:F2}</td><td>{VEH_3S2_L2:F1}</td><td>{VEH_3S2_L3:F2}</td><td>{VEH_3S2_U:F2}</td></tr>");
            t.AppendLine("</table>");
            return t.ToString();
        }

        // Tabla 5.7 INVIAS (pág. 159) - Espacio lateral de seguridad C según ancho de calzada
        // en tangente (AT).
        private string ConstruirTablaEspacioLateralSeguridad(double atResaltado)
        {
            (double at, double c)[] filas = { (6.00, 0.60), (6.60, 0.75), (7.20, 0.90) };
            double atClamp = Math.Max(6.00, Math.Min(7.20, atResaltado));
            double atMasCercano = filas.OrderBy(f => Math.Abs(f.at - atClamp)).First().at;
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='3'>Tabla 5.7 MDG INVIAS 2008 — Valor de C en función del ancho de calzada en tangente</th></tr>");
            t.AppendLine("<tr><th>Ancho de calzada en tangente A<sub>T</sub> (m)</th><th colspan='2'>C (m)</th></tr>");
            foreach (var (at, c) in filas)
            {
                string r = Math.Abs(at - atMasCercano) < 0.001 ? " style='background-color:#ffe9a8; font-weight:bold;'" : "";
                t.AppendLine($"<tr><td{r}>{at:F2}</td><td colspan='2'{r}>{c:F2}</td></tr>");
            }
            t.AppendLine("</table>");
            t.AppendLine("<p class='cita'>Nota: para calzada de ancho diferente a los tres valores tabulados, el Manual indica interpolar. Este asistente interpola linealmente y acota (clamp) A<sub>T</sub> al rango [6.00, 7.20] m, por ser el único rango en el que el Manual autoriza interpolar.</p>");
            return t.ToString();
        }

        // Numeral 2.2 y Figuras 2.2 a 2.7 INVIAS - Dimensiones de carrocería (longitud,
        // ancho, distancia entre ejes y volados) y ángulo máximo de dirección de los
        // vehículos de diseño tipo. Se transcriben SOLO las cotas de carrocería, que están
        // acotadas sin ambigüedad en cada figura; se omiten deliberadamente los radios de
        // giro (trayectoria de rueda / trayectoria de carrocería) que el Manual dibuja como
        // abanico de líneas desde el punto de pivote, porque no son directamente comparables
        // entre figuras (para el vehículo articulado 3S2, el menor de esos radios no
        // corresponde a un "radio mínimo de giro" sino a otra cota geométrica del punto de
        // articulación) y transcribirlos sin la figura original induciría a error. Para el
        // detalle completo de trayectorias de giro, remitirse a las Figuras 2.2 a 2.7 del
        // Manual impreso. La distancia entre ejes del Camión C2 se corrigió de 6.40 a 6.60 m:
        // la Tabla 5.5 (dato tipiado, no manuscrito) fija a=6.60 para "Camión de dos ejes" y
        // se adopta como fuente autoritativa sobre la lectura visual de la Figura 2.5.
        private string ConstruirTablaVehiculosDiseno(int vehiculoResaltado)
        {
            // vehiculoResaltado: índice de CmbVehiculo (0=C2, 1=C3, 2=T3-S2) o -1 para ninguno.
            // idxFila: fila correspondiente en este arreglo (Camión Categoría 2 / 3 / 3S2).
            (string nombre, string figura, double longitud, double ancho, double voladoDel, double entreEjes, double voladoTras, double angulo, int idxFila)[] filas = {
                ("Vehículo liviano", "Fig. 2.2", 5.00, 1.80, 0.80, 2.90, 1.30, 35.0, -1),
                ("Bus mediano", "Fig. 2.3", 10.91, 2.44, 0.76, 6.49, 3.66, 37.1, -1),
                ("Bus grande", "Fig. 2.4", 13.00, 2.60, 2.70, 7.00, 3.30, 46.0, -1),
                ("Camión Categoría 2 (C2)", "Fig. 2.5", 11.00, 2.50, 1.40, 6.60, 3.20, 35.5, 0),
                ("Camión Categoría 3 (C3)", "Fig. 2.6", 11.00, 2.50, 1.25, 6.55, 3.20, 37.0, 1),
                ("Camión Categoría 3S2 (articulado)", "Fig. 2.7", 20.89, 2.59, 1.22, 5.95, 1.38, 28.4, 2),
            };
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='8'>Figuras 2.2 a 2.7 MDG INVIAS 2008 — Dimensiones de los vehículos de diseño</th></tr>");
            t.AppendLine("<tr><th>Vehículo</th><th>Figura</th><th>Longitud total (m)</th><th>Ancho (m)</th><th>Volado delantero (m)</th><th>Distancia entre ejes (m)</th><th>Volado trasero (m)</th><th>Ángulo máx. de dirección (°)</th></tr>");
            foreach (var f in filas)
            {
                string r = vehiculoResaltado >= 0 && f.idxFila == vehiculoResaltado ? " style='background-color:#ffe9a8; font-weight:bold;'" : "";
                string voladoTrasEtiqueta = f.idxFila == 2 ? $"{f.voladoTras:F2} (remolque)" : f.voladoTras.ToString("F2");
                string entreEjesEtiqueta = f.idxFila == 2 ? $"{f.entreEjes:F2} (tractor) + 12.34 (remolque)" : f.entreEjes.ToString("F2");
                t.AppendLine($"<tr><td{r}>{f.nombre}</td><td{r}>{f.figura}</td><td{r}>{f.longitud:F2}</td><td{r}>{f.ancho:F2}</td><td{r}>{f.voladoDel:F2}</td><td{r}>{entreEjesEtiqueta}</td><td{r}>{voladoTrasEtiqueta}</td><td{r}>{f.angulo:F1}</td></tr>");
            }
            t.AppendLine("</table>");
            t.AppendLine("<p class='cita'>Nota: para el vehículo articulado 3S2 (Fig. 2.7), la longitud total de 20.89 m se compone de volado delantero del tractor (1.22 m) + distancia entre ejes del tractor (5.95 m) + distancia del punto de articulación al eje del remolque (12.34 m) + volado trasero del remolque (1.38 m); el ángulo de articulación máximo entre tractor y remolque es de 70.0°. No se reproducen aquí los radios de giro (trayectoria de rueda y de carrocería) de cada figura: remitirse al Manual impreso para el detalle completo de las trayectorias de giro por vehículo.</p>");
            return t.ToString();
        }

        private string ResaltarSiIndiceCoincide(int idx, int resaltado) =>
            idx == resaltado ? " style='background-color:#ffe9a8; font-weight:bold;'" : "";

        // Tabla 3.2/3.3 INVIAS - Radio mínimo de curvatura (RCmín) por VCH, para el emáx del
        // proyecto (8% Primaria/Secundaria, 6% Terciaria). Se genera con la misma fórmula ya
        // verificada de CalcularRadioMinimo, para el rango de VCH válido de cada emáx.
        private string ConstruirTablaRadioMinimo(double vtrResaltado, int catIdx)
        {
            bool terciaria = catIdx == 3;
            double[] vs = terciaria ? new double[] { 20, 30, 40, 50, 60 } : new double[] { 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
            StringBuilder t = new StringBuilder();
            t.AppendLine($"<table><tr><th colspan='{vs.Length + 1}'>Tabla {(terciaria ? "3.3" : "3.2")} MDG INVIAS 2008 — Radio mínimo de curvatura R<sub>Cmín</sub> (m) para e<sub>máx</sub> = {(terciaria ? "6" : "8")}%</th></tr>");
            t.Append("<tr><th>V<sub>CH</sub> (km/h)</th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v:F0}</th>");
            t.AppendLine("</tr><tr><th>R<sub>Cmín</sub> (m)</th>");
            foreach (var v in vs) t.Append($"<td{ResaltarSiCoincide(v, vtrResaltado)}>{CalcularRadioMinimo(v, catIdx):F0}</td>");
            t.AppendLine("</tr></table>");
            return t.ToString();
        }

        // Tabla 4.2 INVIAS - Pendiente longitudinal máxima (%) según categoría y VTV. Se
        // reproduce con blancos donde el Manual no define combinación (fuera del rango de esa
        // categoría), igual que en la tabla original.
        private string ConstruirTablaPendienteMaxima(double vtrResaltado, int catIdxResaltado)
        {
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
            (string nombre, int catIdx)[] categorias = { ("Primaria de dos calzadas", 0), ("Primaria de una calzada", 1), ("Secundaria", 2), ("Terciaria", 3) };
            StringBuilder t = new StringBuilder();
            t.AppendLine($"<table><tr><th colspan='{vs.Length + 1}'>Tabla 4.2 MDG INVIAS 2008 — Pendiente longitudinal máxima (%)</th></tr>");
            t.Append("<tr><th>Categoría \\ V<sub>TV</sub> (km/h)</th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v:F0}</th>");
            t.AppendLine("</tr>");
            foreach (var (nombre, catIdx) in categorias)
            {
                t.Append($"<tr><td{ResaltarSiIndiceCoincide(catIdx, catIdxResaltado)}>{nombre}</td>");
                foreach (var v in vs)
                {
                    bool fueraDeRango = (catIdx == 0 && v < 70) || (catIdx == 1 && v < 60) || (catIdx == 2 && (v < 40 || v > 90)) || (catIdx == 3 && v > 60);
                    string celda = fueraDeRango ? "-" : ObtenerPendienteMaximaINVIAS(catIdx, v).ToString("F0");
                    string resalte = (catIdx == catIdxResaltado) ? ResaltarSiCoincide(v, vtrResaltado) : "";
                    t.Append($"<td{resalte}>{celda}</td>");
                }
                t.AppendLine("</tr>");
            }
            t.AppendLine("</table>");
            return t.ToString();
        }

        // Tabla 3.7 INVIAS - Variación de la aceleración centrífuga (J, m/s³)
        private string ConstruirTablaAceleracionCentrifuga(double vtrResaltado)
        {
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130 };
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='12'>Tabla 3.7 MDG INVIAS 2008 — Variación de la aceleración centrífuga (J, m/s³)</th></tr>");
            t.Append("<tr><th>V<sub>CH</sub> (km/h)</th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v:F0}</th>");
            t.AppendLine("</tr><tr><th>J (m/s³)</th>");
            foreach (var v in vs) t.Append($"<td{ResaltarSiCoincide(v, vtrResaltado)}>{ObtenerVariacionAceleracionCentrifuga(v):F1}</td>");
            t.AppendLine("</tr></table>");
            return t.ToString();
        }

        // Tabla 5.1 INVIAS - Ancho de zona o derecho de vía (rango recomendado, m)
        private string ConstruirTablaDerechoVia(int catIdxResaltado)
        {
            (string nombre, string rango)[] filas = { ("Primaria de dos calzadas", "> 30"), ("Primaria de una calzada", "24 - 30"), ("Secundaria", "20 - 24"), ("Terciaria", "12") };
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='2'>Tabla 5.1 MDG INVIAS 2008 — Ancho de zona o derecho de vía</th></tr>");
            t.AppendLine("<tr><th>Categoría de la carretera</th><th>Ancho de zona (m)</th></tr>");
            for (int i = 0; i < filas.Length; i++)
                t.AppendLine($"<tr><td{ResaltarSiIndiceCoincide(i, catIdxResaltado)}>{filas[i].nombre}</td><td{ResaltarSiIndiceCoincide(i, catIdxResaltado)}>{filas[i].rango}</td></tr>");
            t.AppendLine("</table>");
            return t.ToString();
        }

        // Tabla 5.3 INVIAS - Bombeo de la calzada según tipo de superficie de rodadura
        private string ConstruirTablaBombeo()
        {
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='2'>Tabla 5.3 MDG INVIAS 2008 — Bombeo de la calzada</th></tr>");
            t.AppendLine("<tr><th>Tipo de superficie de rodadura</th><th>Bombeo (%)</th></tr>");
            t.AppendLine("<tr><td style='background-color:#ffe9a8; font-weight:bold;'>Superficie de concreto hidráulico o asfáltico</td><td style='background-color:#ffe9a8; font-weight:bold;'>2</td></tr>");
            t.AppendLine("<tr><td>Tratamientos superficiales</td><td>2 - 3</td></tr>");
            t.AppendLine("<tr><td>Superficie de tierra o grava</td><td>2 - 4</td></tr>");
            t.AppendLine("</table>");
            return t.ToString();
        }

        // Tablas 5.2 (ancho de calzada) y 5.4 (ancho de bermas) INVIAS: matrices dispersas por
        // categoría/terreno/VTR, transcritas íntegras del Manual (celdas sin valor = "-", igual
        // que en la tabla original: esa combinación categoría/terreno/velocidad no se practica).
        private static readonly Dictionary<(int cat, int ter), double?[]> ANCHO_CALZADA = new Dictionary<(int, int), double?[]> {
            { (0,0), new double?[]{null,null,null,null,null,null,7.30,7.30,7.30,7.30} },
            { (0,1), new double?[]{null,null,null,null,null,null,7.30,7.30,7.30,7.30} },
            { (0,2), new double?[]{null,null,null,null,null,7.30,7.30,7.30,7.30,null} },
            { (0,3), new double?[]{null,null,null,null,null,7.30,7.30,7.30,null,null} },
            { (1,0), new double?[]{null,null,null,null,null,null,7.30,7.30,7.30,7.30} },
            { (1,1), new double?[]{null,null,null,null,null,null,7.30,7.30,7.30,7.30} },
            { (1,2), new double?[]{null,null,null,null,7.30,7.30,7.30,7.30,null,null} },
            { (1,3), new double?[]{null,null,null,null,7.00,7.00,7.00,null,null,null} },
            { (2,0), new double?[]{null,null,null,null,null,7.30,7.30,7.30,null,null} },
            { (2,1), new double?[]{null,null,null,7.00,7.30,7.30,7.30,null,null,null} },
            { (2,2), new double?[]{null,null,6.60,7.00,7.00,null,null,null,null,null} },
            { (2,3), new double?[]{null,null,6.00,6.60,7.00,null,null,null,null,null} },
            { (3,0), new double?[]{null,null,null,6.00,null,null,null,null,null,null} },
            { (3,1), new double?[]{null,6.00,6.00,null,null,null,null,null,null,null} },
            { (3,2), new double?[]{6.00,6.00,6.00,null,null,null,null,null,null,null} },
            { (3,3), new double?[]{6.00,6.00,null,null,null,null,null,null,null,null} },
        };

        private static readonly Dictionary<(int cat, int ter), string[]> ANCHO_BERMA = new Dictionary<(int, int), string[]> {
            { (0,0), new[]{"-","-","-","-","-","-","2.5/1.0","2.5/1.0","2.5/1.0","2.5/1.0"} },
            { (0,1), new[]{"-","-","-","-","-","-","2.0/1.0","2.0/1.0","2.5/1.0","2.5/1.0"} },
            { (0,2), new[]{"-","-","-","-","-","1.8/0.5","1.8/0.5","1.8/0.5","2.0/1.0","-"} },
            { (0,3), new[]{"-","-","-","-","-","1.8/0.5","1.8/0.5","-","-","-"} },
            { (1,0), new[]{"-","-","-","-","-","-","2.00","2.00","2.50","-"} },
            { (1,1), new[]{"-","-","-","-","-","1.80","2.00","2.00","2.50","-"} },
            { (1,2), new[]{"-","-","-","-","1.50","1.50","1.80","1.80","-","-"} },
            { (1,3), new[]{"-","-","-","-","1.50","1.50","1.80","-","-","-"} },
            { (2,0), new[]{"-","-","-","-","1.00","1.50","1.80","-","-","-"} },
            { (2,1), new[]{"-","-","-","1.00","1.50","1.50","1.80","-","-","-"} },
            { (2,2), new[]{"-","-","0.50","1.00","1.00","-","-","-","-","-"} },
            { (2,3), new[]{"-","-","0.50","0.50","-","-","-","-","-","-"} },
            { (3,0), new[]{"-","-","1.00","-","-","-","-","-","-","-"} },
            { (3,1), new[]{"-","0.50","1.00","-","-","-","-","-","-","-"} },
            { (3,2), new[]{"0.50","0.50","0.50","-","-","-","-","-","-","-"} },
            { (3,3), new[]{"0.50","0.50","0.50","-","-","-","-","-","-","-"} },
        };

        private string ConstruirTablaAnchoCalzada(int catIdxResaltado, int terIdxResaltado, double vtrResaltado)
        {
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110 };
            string[] categorias = { "Primaria de dos calzadas", "Primaria de una calzada", "Secundaria", "Terciaria" };
            string[] terrenos = { "Plano", "Ondulado", "Montañoso", "Escarpado" };
            StringBuilder t = new StringBuilder();
            t.AppendLine($"<table><tr><th colspan='{vs.Length + 2}'>Tabla 5.2 MDG INVIAS 2008 — Ancho de calzada (m)</th></tr>");
            t.Append("<tr><th>Categoría</th><th>Terreno</th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v:F0}</th>");
            t.AppendLine("</tr>");
            for (int cat = 0; cat < 4; cat++)
            {
                for (int ter = 0; ter < 4; ter++)
                {
                    bool filaActiva = cat == catIdxResaltado && ter == terIdxResaltado;
                    string resalteFila = filaActiva ? " style='background-color:#ffe9a8; font-weight:bold;'" : "";
                    t.Append($"<tr>{(ter == 0 ? $"<td rowspan='4'{resalteFila}>{categorias[cat]}</td>" : "")}<td{resalteFila}>{terrenos[ter]}</td>");
                    double?[] fila = ANCHO_CALZADA[(cat, ter)];
                    for (int i = 0; i < vs.Length; i++)
                    {
                        string resalteCelda = filaActiva && vs[i] == vtrResaltado ? " style='background-color:#ffc94d; font-weight:bold;'" : resalteFila;
                        t.Append($"<td{resalteCelda}>{(fila[i].HasValue ? fila[i]!.Value.ToString("F2") : "-")}</td>");
                    }
                    t.AppendLine("</tr>");
                }
            }
            t.AppendLine("</table>");
            return t.ToString();
        }

        private string ConstruirTablaAnchoBermas(int catIdxResaltado, int terIdxResaltado, double vtrResaltado)
        {
            double[] vs = { 20, 30, 40, 50, 60, 70, 80, 90, 100, 110 };
            string[] categorias = { "Primaria de dos calzadas¹", "Primaria de una calzada", "Secundaria", "Terciaria²" };
            string[] terrenos = { "Plano", "Ondulado", "Montañoso", "Escarpado" };
            StringBuilder t = new StringBuilder();
            t.AppendLine($"<table><tr><th colspan='{vs.Length + 2}'>Tabla 5.4 MDG INVIAS 2008 — Ancho de bermas (m)</th></tr>");
            t.Append("<tr><th>Categoría</th><th>Terreno</th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v:F0}</th>");
            t.AppendLine("</tr>");
            for (int cat = 0; cat < 4; cat++)
            {
                for (int ter = 0; ter < 4; ter++)
                {
                    bool filaActiva = cat == catIdxResaltado && ter == terIdxResaltado;
                    string resalteFila = filaActiva ? " style='background-color:#ffe9a8; font-weight:bold;'" : "";
                    t.Append($"<tr>{(ter == 0 ? $"<td rowspan='4'{resalteFila}>{categorias[cat]}</td>" : "")}<td{resalteFila}>{terrenos[ter]}</td>");
                    string[] fila = ANCHO_BERMA[(cat, ter)];
                    for (int i = 0; i < vs.Length; i++)
                    {
                        string resalteCelda = filaActiva && vs[i] == vtrResaltado ? " style='background-color:#ffc94d; font-weight:bold;'" : resalteFila;
                        t.Append($"<td{resalteCelda}>{fila[i]}</td>");
                    }
                    t.AppendLine("</tr>");
                }
            }
            t.AppendLine("<tr><td colspan='12' style='text-align:left; border:none; font-size:8.5pt;'>¹ Berma derecha/Berma izquierda &nbsp;&nbsp; ² Berma cuneta</td></tr>");
            t.AppendLine("</table>");
            return t.ToString();
        }

        // Tabla 2.1 INVIAS - Velocidad de Diseño de los Tramos Homogéneos: el Manual la
        // presenta como un RANGO admisible por celda (con celdas sombreadas indicando el rango
        // válido, sin un único valor). Este asistente adopta el extremo SUPERIOR de ese rango
        // como valor de diseño (mejor nivel de servicio posible para esa categoría/terreno); la
        // tabla siguiente documenta el valor efectivamente adoptado por el asistente para cada
        // combinación, no el rango completo del Manual (ver numeral 2.1.2 y Tabla 2.1 original
        // para el rango admisible completo).
        private string ConstruirTablaVelocidadDiseno(int catIdxResaltado, int terIdxResaltado)
        {
            string[] categorias = { "Primaria de dos calzadas", "Primaria de una calzada", "Secundaria", "Terciaria" };
            string[] terrenos = { "Plano", "Ondulado", "Montañoso", "Escarpado" };
            int[,] valores = {
                { 110, 100, 80, 70 },
                { 90, 80, 70, 60 },
                { 80, 70, 60, 40 },
                { 50, 40, 30, 20 },
            };
            StringBuilder t = new StringBuilder();
            t.AppendLine("<table><tr><th colspan='6'>Tabla 2.1 MDG INVIAS 2008 (adaptada) — V<sub>TR</sub> adoptada por el asistente (extremo superior del rango del Manual)</th></tr>");
            t.AppendLine("<tr><th>Categoría</th><th>Plano</th><th>Ondulado</th><th>Montañoso</th><th>Escarpado</th></tr>");
            for (int cat = 0; cat < 4; cat++)
            {
                string resalteFila = cat == catIdxResaltado ? " style='background-color:#ffe9a8; font-weight:bold;'" : "";
                t.Append($"<tr><td{resalteFila}>{categorias[cat]}</td>");
                for (int ter = 0; ter < 4; ter++)
                {
                    string resalteCelda = (cat == catIdxResaltado && ter == terIdxResaltado) ? " style='background-color:#ffc94d; font-weight:bold;'" : resalteFila;
                    t.Append($"<td{resalteCelda}>{valores[cat, ter]}</td>");
                }
                t.AppendLine("</tr>");
            }
            t.AppendLine("</table>");
            return t.ToString();
        }

        // Tablas 3.4/3.5 completas (peralte por radio) — se reproducen íntegras porque, desde
        // la incorporación del numeral 3.1.3.5, son la fuente directa del peralte de cada curva.
        private string ConstruirTablaPeraltePorRadio(int catIdx, double vtrResaltado, double radioResaltado)
        {
            bool terciaria = catIdx == 3;
            Dictionary<int, double[]> tabla = terciaria ? RADIOS_EMAX6 : RADIOS_EMAX8;
            double[] eArr = terciaria ? E_TABLA_6 : E_TABLA_8;
            int[] vs = tabla.Keys.OrderBy(k => k).ToArray();
            StringBuilder t = new StringBuilder();
            t.AppendLine($"<table><tr><th colspan='{vs.Length + 1}'>Tabla {(terciaria ? "3.5" : "3.4")} MDG INVIAS 2008 — Radios (R<sub>C</sub>) según V<sub>CH</sub> y Peraltes (e) para e<sub>máx</sub> = {(terciaria ? "6" : "8")}%</th></tr>");
            t.Append("<tr><th>e (%) \\ V<sub>CH</sub></th>");
            foreach (var v in vs) t.Append($"<th{ResaltarSiCoincide(v, vtrResaltado)}>{v}</th>");
            t.AppendLine("</tr>");
            for (int i = 0; i < eArr.Length; i++)
            {
                t.Append($"<tr><td>{eArr[i]:F1}</td>");
                foreach (var v in vs)
                {
                    double r = tabla[v][i];
                    bool esCelda = Math.Abs(r - radioResaltado) < 0.5 && v == vtrResaltado;
                    t.Append($"<td{(esCelda ? " style='background-color:#ffc94d; font-weight:bold;'" : "")}>{r:F0}</td>");
                }
                t.AppendLine("</tr>");
            }
            t.AppendLine("</table>");
            return t.ToString();
        }

        private string FormatearAbscisa(double station) {
            int km = (int)(station / 1000); double m = station % 1000; return $"K{km}+{m:000.00}";
        }

        private string FormatearGMS(double valDeg) {
            int d = (int)valDeg; double restM = (Math.Abs(valDeg) - Math.Abs(d)) * 60.0; int m = (int)restM; double s = (restM - m) * 60.0; return $"{d}°{m:00}'{s:00.0}\"";
        }
    }
}
