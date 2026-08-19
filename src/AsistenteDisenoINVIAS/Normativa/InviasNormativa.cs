using System;

namespace AsistenteDisenoINVIAS.Normativa
{
    /// <summary>
    /// Punto único de verdad para los umbrales del Manual de Diseño Geométrico de
    /// Carreteras de INVIAS (Colombia) que usa el asistente. Antes estas
    /// constantes vivían repartidas y duplicadas dentro de MainWindow.xaml.cs;
    /// centralizarlas permite que Planta, Perfil, Transversal y la Memoria Word
    /// citen exactamente los mismos valores.
    ///
    /// IMPORTANTE: los valores numéricos reproducen las tablas de radios mínimos,
    /// peraltes máximos, pendientes máximas, coeficientes K y distancias de
    /// visibilidad de parada de las ediciones habituales del Manual INVIAS
    /// (aproximación AASHTO con peralte máximo 8%). Antes de emitir planos o
    /// memorias con validez contractual, verifique estos valores contra la
    /// edición vigente del Manual que le aplique a su proyecto.
    /// </summary>
    public static class InviasNormativa
    {
        // ---------------------------------------------------------------
        // Velocidad específica por defecto (Categoría de vía x Tipo de terreno)
        // ---------------------------------------------------------------
        public static double VelocidadPorDefecto(int categoriaIdx, int terrenoIdx)
        {
            // categoriaIdx: 0 Primaria 2 calzadas, 1 Primaria 1 calzada, 2 Secundaria, 3 Terciaria
            // terrenoIdx:   0 Plano, 1 Ondulado, 2 Montañoso, 3 Escarpado
            switch (categoriaIdx)
            {
                case 0: return terrenoIdx == 0 ? 110 : terrenoIdx == 1 ? 100 : terrenoIdx == 2 ? 80 : 70;
                case 1: return terrenoIdx == 0 ? 90 : terrenoIdx == 1 ? 80 : terrenoIdx == 2 ? 70 : 60;
                case 2: return terrenoIdx == 0 ? 80 : terrenoIdx == 1 ? 70 : terrenoIdx == 2 ? 60 : 40;
                default: return terrenoIdx == 0 ? 50 : terrenoIdx == 1 ? 40 : terrenoIdx == 2 ? 30 : 20;
            }
        }

        // ---------------------------------------------------------------
        // Pendiente longitudinal máxima admisible (Tabla 4.2, %)
        // ---------------------------------------------------------------
        public static double PendienteMaxima(int categoriaIdx, double vtr)
        {
            int v = (int)vtr;
            switch (categoriaIdx)
            {
                case 0: return v >= 120 ? 4.0 : v >= 100 ? 5.0 : 6.0;
                case 1: return v >= 100 ? 5.0 : v >= 80 ? 6.0 : v >= 70 ? 7.0 : 8.0;
                case 2: return v >= 80 ? 6.0 : v >= 70 ? 7.0 : v >= 60 ? 8.0 : v >= 50 ? 9.0 : 10.0;
                default: return v <= 20 ? 14.0 : v == 30 ? 12.0 : 10.0;
            }
        }

        // ---------------------------------------------------------------
        // Radio mínimo absoluto en planta (Tabla 3.5, e_max = 8 %), en metros.
        // Se interpola linealmente entre los quiebres de la tabla para
        // velocidades intermedias no tabuladas explícitamente.
        // ---------------------------------------------------------------
        private static readonly (double V, double R)[] TablaRadioMinimo =
        {
            (20, 15), (30, 25), (40, 45), (50, 73), (60, 113), (70, 168),
            (80, 229), (90, 280), (100, 375), (110, 470), (120, 585), (130, 720)
        };

        public static double RadioMinimo(double vtr)
        {
            return InterpolarTabla(TablaRadioMinimo, vtr);
        }

        // ---------------------------------------------------------------
        // Peralte máximo asociado al radio (aproximación continua de la Tabla
        // 3.5: a menor radio, mayor peralte requerido, con techo 8 % y piso 2 %
        // que corresponde al bombeo normal de la calzada).
        // ---------------------------------------------------------------
        public static double PeralteMaximo(double radio)
        {
            if (radio <= 0) return 8.0;
            return Math.Min(8.0, Math.Max(2.0, 8.0 * (45.0 / radio)));
        }

        // ---------------------------------------------------------------
        // Fricción transversal máxima admisible para sobreancho / peralte,
        // decreciente con la velocidad (Tabla 3.4 aprox.).
        // ---------------------------------------------------------------
        public static double FriccionTransversalMaxima(double vtr)
        {
            if (vtr <= 30) return 0.70;
            if (vtr <= 40) return 0.65;
            if (vtr <= 50) return 0.60;
            return 0.50;
        }

        // ---------------------------------------------------------------
        // Sobreancho de calzada en curva (fórmula oficial INVIAS para
        // pavimento de 2 carriles): S = 2·(R - sqrt(R² - L²)) + Vtr / (10·sqrt(R))
        // ---------------------------------------------------------------
        public static double Sobreancho(double radio, double longitudVehiculo, double vtr)
        {
            double b = Math.Sqrt(Math.Max(0.01, radio * radio - longitudVehiculo * longitudVehiculo));
            return 2.0 * (radio - b) + vtr / (10.0 * Math.Sqrt(radio));
        }

        // ---------------------------------------------------------------
        // Longitud de transición de peralte / sobreancho (rotación sobre el
        // eje de la calzada): Lt = ancho_carril * e_max / pendiente_relativa_max
        // ---------------------------------------------------------------
        public static double LongitudTransicion(double anchoCarril, double peralteMaximoPorcentaje, double friccionOPendienteRelativaMax)
        {
            return (anchoCarril * peralteMaximoPorcentaje) / friccionOPendienteRelativaMax;
        }

        // ---------------------------------------------------------------
        // Longitud mínima de tangente / entretangencia (Tabla 4.3), m.
        // ---------------------------------------------------------------
        private static readonly (double V, double L)[] TablaTangenteMinima =
        {
            (20, 40), (30, 60), (40, 80), (50, 140), (60, 170), (70, 195),
            (80, 225), (90, 260), (100, 290), (110, 320), (120, 350), (130, 380)
        };

        public static double LongitudMinimaTangente(double vtr)
        {
            return InterpolarTabla(TablaTangenteMinima, vtr);
        }

        // ---------------------------------------------------------------
        // Coeficientes K mínimos de curva vertical (Tabla 4.4 cresta / 4.5
        // columpio, criterio de comodidad + visibilidad de parada), en m/%.
        // ---------------------------------------------------------------
        private static readonly (double V, double K)[] TablaKCresta =
        {
            (20, 1), (30, 2), (40, 4), (50, 7), (60, 11), (70, 17),
            (80, 26), (90, 39), (100, 55), (110, 76), (120, 102), (130, 135)
        };

        private static readonly (double V, double K)[] TablaKColumpio =
        {
            (20, 2), (30, 4), (40, 7), (50, 11), (60, 15), (70, 20),
            (80, 26), (90, 32), (100, 39), (110, 46), (120, 53), (130, 61)
        };

        public static double KMinimoCresta(double vtr) => InterpolarTabla(TablaKCresta, vtr);
        public static double KMinimoColumpio(double vtr) => InterpolarTabla(TablaKColumpio, vtr);

        // Diferencia algebraica mínima de pendientes a partir de la cual se
        // exige curva vertical, y longitud mínima absoluta por comodidad visual.
        public const double DiferenciaAlgebraicaMinima = 0.5; // %
        public static double LongitudMinimaVisual(double vtr) => 0.6 * vtr;

        // ---------------------------------------------------------------
        // Distancia de visibilidad de parada Dp (AASHTO/INVIAS), en metros.
        // Dp = 0.278 * V * t + V² / (254 * (fl ± p))
        // t: tiempo de percepción-reacción (2.5 s), p: pendiente en decimal
        // (positiva en bajada, negativa en subida sobre el vehículo que frena).
        // ---------------------------------------------------------------
        private static readonly (double V, double f)[] TablaFriccionLongitudinal =
        {
            (20, 0.44), (30, 0.40), (40, 0.38), (50, 0.35), (60, 0.33), (70, 0.31),
            (80, 0.30), (90, 0.29), (100, 0.28), (110, 0.28), (120, 0.28), (130, 0.28)
        };

        public static double DistanciaVisibilidadParada(double vtr, double pendienteDecimal = 0.0, double tiempoReaccion = 2.5)
        {
            double fl = InterpolarTabla(TablaFriccionLongitudinal, vtr);
            double denom = fl + pendienteDecimal;
            if (denom < 0.05) denom = 0.05;
            return 0.278 * vtr * tiempoReaccion + (vtr * vtr) / (254.0 * denom);
        }

        // Longitud mínima de curva vertical por visibilidad de parada (fórmulas
        // AASHTO en unidades métricas, usadas como verificación complementaria
        // al criterio de comodidad K de las tablas 4.4/4.5):
        //   Cresta   (h1=1.08 m, h2=0.15 m): L = A·S²/404   si S < L
        //                                    L = 2S - 404/A si S ≥ L
        //   Columpio (criterio del haz de luz, ángulo 1°):  L = A·S²/(120+3.5S) si S < L
        //                                                    L = 2S-(120+3.5S)/A si S ≥ L
        public static double LongitudMinimaPorVisibilidad(double diferenciaAlgebraica, double dp, bool esCresta)
        {
            double a = Math.Max(diferenciaAlgebraica, 0.01);
            if (dp <= 0) return 0.0;

            if (esCresta)
            {
                double lLargo = (a * dp * dp) / 404.0;
                if (lLargo >= dp) return lLargo;
                return Math.Max(0.0, 2.0 * dp - 404.0 / a);
            }
            else
            {
                double lLargo = (a * dp * dp) / (120.0 + 3.5 * dp);
                if (lLargo >= dp) return lLargo;
                return Math.Max(0.0, 2.0 * dp - (120.0 + 3.5 * dp) / a);
            }
        }

        // ---------------------------------------------------------------
        private static double InterpolarTabla((double V, double Valor)[] tabla, double vtr)
        {
            if (vtr <= tabla[0].V) return tabla[0].Valor;
            if (vtr >= tabla[tabla.Length - 1].V) return tabla[tabla.Length - 1].Valor;

            for (int i = 0; i < tabla.Length - 1; i++)
            {
                var (v0, val0) = tabla[i];
                var (v1, val1) = tabla[i + 1];
                if (vtr >= v0 && vtr <= v1)
                {
                    if (Math.Abs(v1 - v0) < 1e-6) return val0;
                    double t = (vtr - v0) / (v1 - v0);
                    return val0 + t * (val1 - val0);
                }
            }
            return tabla[tabla.Length - 1].Valor;
        }
    }
}
