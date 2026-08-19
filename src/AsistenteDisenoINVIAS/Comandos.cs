using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

// Se elimina [assembly: ExtensionApplication(null)] para permitir la carga correcta en .NET 8
[assembly: CommandClass(typeof(AsistenteDisenoINVIAS.Comandos))]

namespace AsistenteDisenoINVIAS
{
    public class Comandos
    {
        private static PaletteSet? _paletteSet = null;

        [CommandMethod("INVIAS_DISENO")]
        public void AbrirAsistente()
        {
            if (_paletteSet == null)
            {
                _paletteSet = new PaletteSet("Asistente de Diseño Vial INVIAS", new Guid("8E12A345-1234-4567-89AB-CDEF12345678"));
                _paletteSet.AddVisual("Diseño Geométrico", new MainWindow());
                _paletteSet.MinimumSize = new System.Drawing.Size(420, 560);
            }

            _paletteSet.Visible = true;
        }
    }
}
