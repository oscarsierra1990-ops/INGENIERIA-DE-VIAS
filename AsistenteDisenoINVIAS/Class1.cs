using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using System;

namespace AsistenteDisenoINVIAS
{
    public class Class1
    {
        // Variable estática para mantener la paleta en memoria durante la sesión
        static PaletteSet? _ps = null;

        [CommandMethod("ASISTENTEINVIAS")]
        public void MostrarAsistente()
        {
            if (_ps == null)
            {
                // Crea la ventana acoplable (PaletteSet) de AutoCAD
                _ps = new PaletteSet("Asistente de Diseño INVIAS");
                _ps.Style = PaletteSetStyles.ShowPropertiesMenu |
                            PaletteSetStyles.ShowAutoHideButton |
                            PaletteSetStyles.ShowCloseButton |
                            PaletteSetStyles.Snappable;

                // Tamaño mínimo recomendado para la interfaz
                _ps.MinimumSize = new System.Drawing.Size(380, 650);

                // Instancia nuestra interfaz WPF
                MainWindow controlWPF = new MainWindow();

                // Agrega la interfaz a la paleta
                _ps.AddVisual("Diseño Normativo", controlWPF);
            }

            // Muestra la paleta en Civil 3D
            _ps.KeepFocus = true;
            _ps.Visible = true;
        }
    }
}