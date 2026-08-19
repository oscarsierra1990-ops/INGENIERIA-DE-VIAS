namespace AsistenteDisenoINVIAS.Modelo
{
    /// <summary>
    /// Resultado consolidado de un PVI/curva vertical de la rasante calculada
    /// automáticamente a partir del terreno natural (MDT).
    /// </summary>
    public class DatosCurvaVertical
    {
        public string Elemento { get; set; } = "";
        public double Abscisa { get; set; }
        public double Cota { get; set; }
        public double PendienteEntrada { get; set; } // g1, %
        public double PendienteSalida { get; set; }  // g2, %
        public double DiferenciaAlgebraica { get; set; } // A = |g2-g1|
        public string Tipo { get; set; } = "N/A"; // Cresta / Columpio / N/A (PVI sin curva)

        public double KAplicado { get; set; }
        public double LongitudCurva { get; set; } // Lv adoptada, m
        public double LongitudMinimaComodidad { get; set; }
        public double LongitudMinimaVisibilidad { get; set; }
        public bool CumpleLongitudMinima { get; set; } = true;

        public string Observaciones { get; set; } = "";
    }
}
