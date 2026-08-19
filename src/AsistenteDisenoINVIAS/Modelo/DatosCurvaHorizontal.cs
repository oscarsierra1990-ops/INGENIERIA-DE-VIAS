namespace AsistenteDisenoINVIAS.Modelo
{
    /// <summary>
    /// Resultado consolidado de una curva circular del alineamiento horizontal:
    /// geometría real dibujada por el usuario + parámetros normativos calculados
    /// (radio mínimo, peralte, sobreancho, longitud de transición). Una misma
    /// instancia se llena en dos pasadas: Pestaña 1 (geometría) y Pestaña 3
    /// (peralte/sobreancho), y se usa íntegramente para la memoria descriptiva.
    /// </summary>
    public class DatosCurvaHorizontal
    {
        public string Elemento { get; set; } = "";
        public double AbscisaInicio { get; set; }
        public double AbscisaFin { get; set; }
        public double Radio { get; set; }
        public double DeltaGrados { get; set; }
        public double Longitud { get; set; }
        public bool GiraDerecha { get; set; }

        public double RadioMinimoNormativo { get; set; }
        public bool CumpleRadioMinimo { get; set; } = true;

        /// <summary>Radio tal como fue dibujado por el usuario, antes de cualquier
        /// corrección automática. 0 si no hubo corrección (Radio ya era el original).</summary>
        public double RadioOriginalDibujado { get; set; }
        /// <summary>True si el asistente amplió automáticamente el radio para cumplir Rmin.</summary>
        public bool CorregidoAutomaticamente { get; set; }

        public double PeralteMaximo { get; set; }      // e, %
        public double SobreanchoMaximo { get; set; }   // S, m
        public double LongitudTransicion { get; set; } // Lt, m

        public string Observaciones { get; set; } = "";
    }
}
