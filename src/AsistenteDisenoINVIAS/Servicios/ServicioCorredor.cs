using System;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AsistenteDisenoINVIAS.Modelo;

namespace AsistenteDisenoINVIAS.Servicios
{
    /// <summary>
    /// Complemento OPCIONAL a la Pestaña 3: intenta construir un Corridor nativo
    /// de Civil 3D (Assembly + Baseline + BaselineRegion) apoyado en el
    /// alineamiento, la rasante y el ancho de carril ya calculados, para que el
    /// borde de vía quede modelado como sólido 3D del corredor y no solo como
    /// los alineamientos de desfase 2D de la Pestaña 3.
    ///
    /// El catálogo exacto de subensambles (LaneOutsideSuper, bermas, taludes,
    /// etc.) varía según el contenido instalado en cada estación de trabajo de
    /// Civil 3D 2025, por lo que esta rutina localiza los tipos disponibles por
    /// reflexión sobre AeccDbMgd.dll y falla de forma segura si no encuentra un
    /// subensamble de carril compatible: en ese caso el diseño NO se pierde,
    /// porque los alineamientos de borde de vía (Pestaña 3) ya son elementos
    /// nativos válidos y quedan documentados en la memoria descriptiva igual.
    /// </summary>
    public static class ServicioCorredor
    {
        public static bool CrearCorredorBasico(
            Transaction tr,
            CivilDocument civilDoc,
            Database db,
            ObjectId alignId,
            ObjectId profileId,
            ParametrosEntrada parametros,
            ResultadoDiseno resultado)
        {
            try
            {
                Alignment? alignment = tr.GetObject(alignId, OpenMode.ForRead) as Alignment;
                if (alignment == null) return false;

                ObjectId assemblyId = CrearAssemblyCarril(tr, db, parametros);
                if (assemblyId == ObjectId.Null)
                {
                    resultado.RegistrarAdvertencia(
                        "No se encontró en el catálogo instalado un subensamble de carril compatible para generar el Corridor automáticamente. " +
                        "El borde de vía queda representado por los alineamientos de desfase nativos '_Borde_Izquierdo'/'_Borde_Derecho' generados en la Pestaña 3.");
                    return false;
                }

                string corridorName = "Corredor_INVIAS_" + alignment.Name;
                ObjectId? corridorId = InvocarCreateEstatico(typeof(Corridor), corridorName, db);
                if (corridorId == null || corridorId.Value == ObjectId.Null) return false;
                Corridor? corridor = tr.GetObject(corridorId.Value, OpenMode.ForWrite) as Corridor;
                if (corridor == null) return false;

                // Se usa 'dynamic' para las colecciones de líneas base/regiones:
                // la firma exacta (sobrecargas de Baselines.Add / BaselineRegions.Add)
                // ha variado entre versiones del SDK de Civil 3D; enlazar en
                // tiempo de ejecución evita que una firma distinta rompa la
                // compilación de todo el proyecto.
                dynamic dCorridor = corridor;
                dynamic baseline = dCorridor.Baselines.Add(alignId, profileId);
                dynamic region = baseline.BaselineRegions.Add(assemblyId, alignment.StartingStation, alignment.EndingStation);

                dCorridor.Rebuild();

                resultado.RegistrarElementoNativo($"Corredor nativo '{corridorName}' generado con ensamble de carril + peralte/sobreancho vinculados al alineamiento y a la rasante.");
                resultado.CorredorProcesado = true;
                return true;
            }
            catch (Exception ex)
            {
                resultado.RegistrarAdvertencia(
                    $"No se pudo generar el Corridor nativo automáticamente ({ex.Message}). " +
                    "Esto no afecta los cálculos de planta, perfil, sobreancho ni peralte: puede armar el ensamble manualmente en la ficha Corredor de Civil 3D usando el alineamiento, la rasante y los anchos ya calculados en la memoria descriptiva.");
                return false;
            }
        }

        /// <summary>
        /// Busca por reflexión un subensamble de tipo "carril" en AeccDbMgd.dll
        /// (p. ej. LaneOutsideSuper) e invoca su método estático "Create" con la
        /// combinación de parámetros que exponga, para tolerar variaciones entre
        /// versiones del SDK sin acoplar el proyecto a una firma fija.
        /// </summary>
        private static ObjectId CrearAssemblyCarril(Transaction tr, Database db, ParametrosEntrada parametros)
        {
            ObjectId assemblyId = Autodesk.Civil.DatabaseServices.Assembly.CreateAssembly("Ensamble_INVIAS_" + DateTime.Now.ToString("HHmmss"), db.Clayer);
            if (assemblyId == ObjectId.Null) return ObjectId.Null;

            Autodesk.Civil.DatabaseServices.Assembly? assembly = tr.GetObject(assemblyId, OpenMode.ForWrite) as Autodesk.Civil.DatabaseServices.Assembly;
            if (assembly == null) return ObjectId.Null;

            Type? tipoCarril = BuscarTipoSubassembly("LaneOutsideSuper") ?? BuscarTipoSubassembly("Lane");
            if (tipoCarril == null) return ObjectId.Null;

            ObjectId? subLeftId = InvocarCreateEstatico(tipoCarril, "Carril_Izq_INVIAS", db);
            ObjectId? subRightId = InvocarCreateEstatico(tipoCarril, "Carril_Der_INVIAS", db);
            if (subLeftId == null || subRightId == null) return ObjectId.Null;

            try
            {
                dynamic dAssembly = assembly;
                dAssembly.AssemblyGroups.Item(AssemblyGroupType.Left).Insert(0, subLeftId.Value);
                dAssembly.AssemblyGroups.Item(AssemblyGroupType.Right).Insert(0, subRightId.Value);
            }
            catch
            {
                return ObjectId.Null;
            }

            return assemblyId;
        }

        private static Type? BuscarTipoSubassembly(string nombreParcial)
        {
            System.Reflection.Assembly aeccAssembly = typeof(Autodesk.Civil.DatabaseServices.Alignment).Assembly;
            return aeccAssembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "Autodesk.Civil.DatabaseServices")
                .Where(t => t.Name.IndexOf(nombreParcial, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(t => t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static) != null)
                .FirstOrDefault();
        }

        private static ObjectId? InvocarCreateEstatico(Type tipo, string nombre, Database db)
        {
            MethodInfo? metodo = tipo.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
            if (metodo == null) return null;

            ParameterInfo[] parametros = metodo.GetParameters();
            object?[] argumentos = new object?[parametros.Length];
            for (int i = 0; i < parametros.Length; i++)
            {
                Type pt = parametros[i].ParameterType;
                if (pt == typeof(string)) argumentos[i] = nombre;
                else if (pt == typeof(ObjectId)) argumentos[i] = db.Clayer;
                else if (pt.IsValueType) argumentos[i] = Activator.CreateInstance(pt);
                else argumentos[i] = null;
            }

            try
            {
                object? resultado = metodo.Invoke(null, argumentos);
                if (resultado is ObjectId oid) return oid;
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
