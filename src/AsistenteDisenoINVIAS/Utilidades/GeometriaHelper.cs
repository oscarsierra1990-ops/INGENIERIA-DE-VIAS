using System;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;

namespace AsistenteDisenoINVIAS.Utilidades
{
    /// <summary>
    /// Funciones puras compartidas por los servicios de Planta, Perfil y
    /// Transversal, y por el generador de la memoria en Word. Antes vivían
    /// duplicadas o dispersas dentro de MainWindow.xaml.cs.
    /// </summary>
    public static class GeometriaHelper
    {
        /// <summary>
        /// Determina si un arco del alineamiento gira a la derecha usando el
        /// signo del producto cruzado entre los vectores centro-inicio y
        /// centro-fin (positivo = sentido antihorario = giro a la izquierda).
        /// </summary>
        public static bool EsGiroDerecha(AlignmentArc arc)
        {
            double v1x = arc.StartPoint.X - arc.CenterPoint.X;
            double v1y = arc.StartPoint.Y - arc.CenterPoint.Y;
            double v2x = arc.EndPoint.X - arc.CenterPoint.X;
            double v2y = arc.EndPoint.Y - arc.CenterPoint.Y;
            return ((v1x * v2y) - (v1y * v2x)) < 0;
        }

        public static string FormatearAbscisa(double station)
        {
            int km = (int)(station / 1000);
            double m = station % 1000;
            return $"K{km}+{m:000.00}";
        }

        public static string FormatearGMS(double valDeg)
        {
            int d = (int)valDeg;
            double restM = (Math.Abs(valDeg) - Math.Abs(d)) * 60.0;
            int m = (int)restM;
            double s = (restM - m) * 60.0;
            return $"{d}°{m:00}'{s:00.0}\"";
        }
    }
}
