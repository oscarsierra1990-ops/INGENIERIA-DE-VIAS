using System.Collections.Generic;

namespace AsistenteDisenoINVIAS.Modelo
{
    /// <summary>
    /// Estado de diseño de la sesión actual del asistente. Se conserva una única
    /// instancia mientras el panel está abierto para que las tres pestañas
    /// acumulen resultados sobre el mismo alineamiento y la pestaña de Memoria
    /// pueda documentar exactamente lo que se generó en el dibujo.
    /// </summary>
    public class ResultadoDiseno
    {
        public ParametrosEntrada Parametros { get; set; } = new ParametrosEntrada();
        public List<DatosCurvaHorizontal> CurvasHorizontales { get; set; } = new List<DatosCurvaHorizontal>();
        public List<DatosCurvaVertical> CurvasVerticales { get; set; } = new List<DatosCurvaVertical>();
        public List<string> Advertencias { get; set; } = new List<string>();
        public List<string> ElementosNativosGenerados { get; set; } = new List<string>();

        public bool PlantaProcesada { get; set; }
        public bool PerfilProcesado { get; set; }
        public bool TransversalProcesado { get; set; }
        public bool CorredorProcesado { get; set; }

        public void RegistrarAdvertencia(string mensaje)
        {
            if (!Advertencias.Contains(mensaje)) Advertencias.Add(mensaje);
        }

        public void RegistrarElementoNativo(string mensaje)
        {
            ElementosNativosGenerados.Add(mensaje);
        }
    }
}
