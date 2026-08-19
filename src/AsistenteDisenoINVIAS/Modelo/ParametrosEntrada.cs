namespace AsistenteDisenoINVIAS.Modelo
{
    /// <summary>
    /// Parámetros de entrada seleccionados por el usuario en el panel, comunes a
    /// planta, perfil y sección transversal. Se conserva una única instancia por
    /// sesión de diseño para que la memoria descriptiva documente exactamente los
    /// mismos valores que se usaron en los cálculos y en la generación CAD.
    /// </summary>
    public class ParametrosEntrada
    {
        public string CategoriaVia { get; set; } = "";
        public int CategoriaViaIdx { get; set; }
        public string TipoTerreno { get; set; } = "";
        public int TipoTerrenoIdx { get; set; }
        public double VelocidadDiseno { get; set; } // Vtr, km/h

        public string NombreAlineamiento { get; set; } = "";
        public double AbscisaInicial { get; set; }
        public double AbscisaFinal { get; set; }

        public string NombreSuperficie { get; set; } = "";

        public double AnchoCarril { get; set; } = 3.65;
        public string VehiculoDiseno { get; set; } = "";
        public double LongitudVehiculo { get; set; }

        // Valores normativos derivados que se documentan en la memoria.
        public double PendienteMaximaAdmisible { get; set; }
        public double RadioMinimoAdmisible { get; set; }
        public double LongitudMinimaTangenteVertical { get; set; }
        public double KMinimoCresta { get; set; }
        public double KMinimoColumpio { get; set; }
        public double PeraltePorDefecto { get; set; } = -2.0; // bombeo normal, %
        public double FriccionTransversalMaxima { get; set; }
    }
}
