using System.Text;
using System.Text.RegularExpressions;

namespace AnalizadorSql.Client.Services;

public sealed class EsquemaSimulado
{
    public List<BaseDatosSimulada> BasesDatos { get; set; } = new();
    public string? BaseDatosActual { get; set; }

    public EsquemaSimulado Copiar() => new()
    {
        BaseDatosActual = BaseDatosActual,
        BasesDatos = BasesDatos.Select(b => b.Copiar()).ToList()
    };
}

public sealed class BaseDatosSimulada
{
    public string Nombre { get; set; } = "";
    public List<TablaSimulada> Tablas { get; set; } = new();
    public BaseDatosSimulada Copiar() => new()
    {
        Nombre = Nombre,
        Tablas = Tablas.Select(t => t.Copiar()).ToList()
    };
}

public sealed class TablaSimulada
{
    public string Nombre { get; set; } = "";
    public string Esquema { get; set; } = "dbo";
    public List<ColumnaSimulada> Columnas { get; set; } = new();
    public List<List<string>> ClavesUnicas { get; set; } = new();
    public List<ClaveForaneaSimulada> ClavesForaneas { get; set; } = new();
    public List<string> Indices { get; set; } = new();
    public List<string> RestriccionesCheck { get; set; } = new();
    public List<List<string>> Registros { get; set; } = new();
    public TablaSimulada Copiar() => new()
    {
        Nombre = Nombre,
        Esquema = Esquema,
        Columnas = Columnas.Select(c => new ColumnaSimulada { Nombre = c.Nombre, Tipo = c.Tipo, EsIdentity = c.EsIdentity }).ToList(),
        ClavesUnicas = ClavesUnicas.Select(k => k.ToList()).ToList(),
        ClavesForaneas = ClavesForaneas.Select(k => new ClaveForaneaSimulada { Nombre = k.Nombre, Columnas = k.Columnas.ToList(), TablaReferenciada = k.TablaReferenciada, ColumnasReferenciadas = k.ColumnasReferenciadas.ToList() }).ToList(),
        Indices = Indices.ToList(),
        RestriccionesCheck = RestriccionesCheck.ToList(),
        Registros = Registros.Select(r => r.ToList()).ToList()
    };
}

public sealed class ClaveForaneaSimulada { public string Nombre { get; set; } = ""; public List<string> Columnas { get; set; } = new(); public string TablaReferenciada { get; set; } = ""; public List<string> ColumnasReferenciadas { get; set; } = new(); }

public sealed class ColumnaSimulada
{
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "";
    public bool EsIdentity { get; set; }
}

public sealed class ErrorSemanticoSimulado
{
    public int Linea { get; set; }
    public int Columna { get; set; } = 1;
    public string Mensaje { get; set; } = "";
}

public sealed class ResultadoSimulacion
{
    public bool Exitoso { get; set; }
    public EsquemaSimulado Esquema { get; set; } = new();
    public List<ErrorSemanticoSimulado> Errores { get; set; } = new();
    public List<string> Operaciones { get; set; } = new();
}

/// <summary>
/// Simulador académico en memoria. No ejecuta SQL en un servidor real.
/// Cambios solo se confirman cuando todas las instrucciones soportadas son válidas.
/// </summary>
public sealed class SimuladorSql
{
    public ResultadoSimulacion Simular(string codigo, EsquemaSimulado esquemaActual)
    {
        var temporal = esquemaActual.Copiar();
        // SQL Server siempre dispone de master; se representa como base del sistema para admitir USE master.
        if (!temporal.BasesDatos.Any(b => Igual(b.Nombre, "master")))
            temporal.BasesDatos.Insert(0, new BaseDatosSimulada { Nombre = "master" });
        var resultado = new ResultadoSimulacion { Esquema = temporal };
        codigo = ResolverIfDbId(codigo, temporal);
        var sentencias = SepararSentencias(codigo);

        foreach (var sentencia in sentencias)
        {
            var sql = sentencia.Texto.Trim();
            if (sql.Length == 0 || Regex.IsMatch(sql, @"^GO$", RegexOptions.IgnoreCase)) continue;

            try
            {
                ProcesarSentencia(sql, sentencia.Linea, temporal, resultado.Operaciones);
            }
            catch (SemanticaException ex)
            {
                resultado.Errores.Add(new ErrorSemanticoSimulado
                {
                    Linea = sentencia.Linea,
                    Columna = 1,
                    Mensaje = ex.Message
                });
            }
        }

        resultado.Exitoso = resultado.Errores.Count == 0;
        // Si hubo errores, devolver el esquema original, nunca la copia parcial.
        resultado.Esquema = resultado.Exitoso ? temporal : esquemaActual;
        return resultado;
    }

    private static void ProcesarSentencia(string sql, int linea, EsquemaSimulado esquema, List<string> operaciones)
    {
        Match m;

        if ((m = Regex.Match(sql, @"^USE\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var nombre = LimpiarIdentificador(m.Groups[1].Value.Trim());
            var bd = BuscarBase(esquema, nombre);
            if (bd == null) throw new SemanticaException($"La base de datos '{nombre}' no existe; no se puede ejecutar USE.");
            esquema.BaseDatosActual = bd.Nombre;
            operaciones.Add($"Contexto cambiado a la base de datos '{bd.Nombre}'.");
            return;
        }

        if ((m = Regex.Match(sql, @"^ALTER\s+DATABASE\s+(.+?)\s+SET\s+SINGLE_USER\b.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var nombre = LimpiarIdentificador(m.Groups[1].Value.Trim());
            if (BuscarBase(esquema, nombre) == null) throw new SemanticaException($"La base de datos '{nombre}' no existe.");
            operaciones.Add($"Configuración SINGLE_USER reconocida para '{nombre}' (simulación; no se modifica un servidor real).");
            return;
        }

        if ((m = Regex.Match(sql, @"^CREATE\s+DATABASE\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var nombre = LimpiarIdentificador(m.Groups[1].Value.Trim());
            if (BuscarBase(esquema, nombre) != null) throw new SemanticaException($"La base de datos '{nombre}' ya existe.");
            esquema.BasesDatos.Add(new BaseDatosSimulada { Nombre = nombre });
            operaciones.Add($"Base de datos creada: {nombre}.");
            return;
        }

        if ((m = Regex.Match(sql, @"^DROP\s+DATABASE\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var nombre = LimpiarIdentificador(m.Groups[1].Value.Trim());
            var bd = BuscarBase(esquema, nombre) ?? throw new SemanticaException($"La base de datos '{nombre}' no existe.");
            if (string.Equals(esquema.BaseDatosActual, bd.Nombre, StringComparison.OrdinalIgnoreCase)) throw new SemanticaException($"No se puede eliminar la base de datos '{nombre}' mientras está seleccionada con USE.");
            esquema.BasesDatos.Remove(bd);
            operaciones.Add($"Base de datos eliminada: {nombre}.");
            return;
        }

        if ((m = Regex.Match(sql, @"^CREATE\s+TABLE\s+(.+?)\s*\((.*)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var destino = ResolverDestinoCreacion(m.Groups[1].Value.Trim(), esquema);
            if (destino.Base.Tablas.Any(t => MismoNombreTabla(t, destino.Esquema, destino.Tabla)))
                throw new SemanticaException($"La tabla '{destino.Esquema}.{destino.Tabla}' ya existe en '{destino.Base.Nombre}'.");

            var tabla = new TablaSimulada { Nombre = destino.Tabla, Esquema = destino.Esquema };
            var clavesPendientes = new List<List<string>>();
            foreach (var definicion in SepararPorComas(m.Groups[2].Value))
            {
                var def = definicion.Trim();
                if (def.Length == 0) continue;
                if (Regex.IsMatch(def, @"^(CONSTRAINT\b|PRIMARY\s+KEY\b|UNIQUE\b)", RegexOptions.IgnoreCase))
                {
                    var km = Regex.Match(def, @"(?:PRIMARY\s+KEY|UNIQUE)\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
                    if (km.Success) clavesPendientes.Add(SepararPorComas(km.Groups[1].Value).Select(x => LimpiarIdentificador(x.Trim())).ToList());
                    var fk = Regex.Match(def, @"(?:CONSTRAINT\s+(?<nombre>\w+)\s+)?FOREIGN\s+KEY\s*\((?<cols>[^)]+)\)\s+REFERENCES\s+(?<tabla>[\w.\[\]]+)\s*\((?<refcols>[^)]+)\)", RegexOptions.IgnoreCase);
                    if (fk.Success) tabla.ClavesForaneas.Add(new ClaveForaneaSimulada { Nombre = fk.Groups["nombre"].Value, Columnas = SepararPorComas(fk.Groups["cols"].Value).Select(x => LimpiarIdentificador(x.Trim())).ToList(), TablaReferenciada = fk.Groups["tabla"].Value, ColumnasReferenciadas = SepararPorComas(fk.Groups["refcols"].Value).Select(x => LimpiarIdentificador(x.Trim())).ToList() });
                    continue;
                }
                if (Regex.IsMatch(def, @"^(FOREIGN\s+KEY|CHECK|INDEX)\b", RegexOptions.IgnoreCase)) continue;
                var cm = Regex.Match(def, @"^(?<col>\[[^\]]+\]|""[^""]+""|[\w#$@]+)\s+(?<tipo>[\w]+(?:\s*\([^)]*\))?)", RegexOptions.IgnoreCase);
                if (!cm.Success) throw new SemanticaException($"No se pudo interpretar la definición de columna '{def}'.");
                var col = LimpiarIdentificador(cm.Groups["col"].Value);
                if (tabla.Columnas.Any(c => Igual(c.Nombre, col))) throw new SemanticaException($"La columna '{col}' está duplicada en la definición de la tabla '{tabla.Nombre}'.");
                tabla.Columnas.Add(new ColumnaSimulada { Nombre = col, Tipo = Regex.Replace(cm.Groups["tipo"].Value, @"\s+", " ").Trim(), EsIdentity = Regex.IsMatch(def, @"\bIDENTITY\b", RegexOptions.IgnoreCase) });
                if (Regex.IsMatch(def, @"\bPRIMARY\s+KEY\b|\bUNIQUE\b", RegexOptions.IgnoreCase)) clavesPendientes.Add(new List<string> { col });
            }
            if (tabla.Columnas.Count == 0) throw new SemanticaException($"La tabla '{tabla.Nombre}' debe tener al menos una columna reconocible.");
            foreach (var clave in clavesPendientes)
            {
                foreach (var colClave in clave)
                    if (!tabla.Columnas.Any(c => Igual(c.Nombre, colClave))) throw new SemanticaException($"La clave de la tabla '{tabla.Nombre}' referencia la columna inexistente '{colClave}'.");
                if (!tabla.ClavesUnicas.Any(k => k.SequenceEqual(clave, StringComparer.OrdinalIgnoreCase))) tabla.ClavesUnicas.Add(clave);
            }
            destino.Base.Tablas.Add(tabla);
            foreach (var fk in tabla.ClavesForaneas) ValidarDefinicionForanea(tabla, fk, esquema);
            operaciones.Add($"Tabla creada: {destino.Base.Nombre}.{tabla.Esquema}.{tabla.Nombre} ({tabla.Columnas.Count} columnas).");
            return;
        }

        if ((m = Regex.Match(sql, @"^ALTER\s+TABLE\s+(?<tabla>.+?)\s+ADD\s+CONSTRAINT\s+(?<nombre>\w+)\s+(?<tipo>FOREIGN\s+KEY|UNIQUE|CHECK)\s*(?<resto>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var d = ResolverTabla(m.Groups["tabla"].Value.Trim(), esquema); var nombre = m.Groups["nombre"].Value; var tipo = Regex.Replace(m.Groups["tipo"].Value, @"\s+", " ").ToUpperInvariant(); var resto = m.Groups["resto"].Value.Trim();
            if (tipo == "FOREIGN KEY")
            {
                var fk = Regex.Match(resto, @"^\((?<cols>[^)]+)\)\s+REFERENCES\s+(?<tabla>[^\s(]+)\s*\((?<refcols>[^)]+)\)$", RegexOptions.IgnoreCase);
                if (!fk.Success) throw new SemanticaException($"Definición de clave foránea '{nombre}' no válida.");
                var clave = new ClaveForaneaSimulada { Nombre = nombre, Columnas = SepararPorComas(fk.Groups["cols"].Value).Select(x => LimpiarIdentificador(x.Trim())).ToList(), TablaReferenciada = fk.Groups["tabla"].Value, ColumnasReferenciadas = SepararPorComas(fk.Groups["refcols"].Value).Select(x => LimpiarIdentificador(x.Trim())).ToList() };
                ValidarDefinicionForanea(d.TablaObjeto, clave, esquema);
                foreach (var row in d.TablaObjeto.Registros) ValidarDatosForaneos(d.TablaObjeto, clave, row, esquema);
                d.TablaObjeto.ClavesForaneas.Add(clave); operaciones.Add($"Clave foránea agregada: {nombre}."); return;
            }
            if (tipo == "UNIQUE")
            {
                var u = Regex.Match(resto, @"^\((?<cols>[^)]+)\)$"); if (!u.Success) throw new SemanticaException($"Definición UNIQUE '{nombre}' no válida.");
                var cols = SepararPorComas(u.Groups["cols"].Value).Select(x => LimpiarIdentificador(x.Trim())).ToList();
                foreach (var c in cols) if (!d.TablaObjeto.Columnas.Any(x => Igual(x.Nombre, c))) throw new SemanticaException($"La columna '{c}' no existe en '{d.Tabla}'.");
                d.TablaObjeto.ClavesUnicas.Add(cols); ValidarClavesUnicas(d.TablaObjeto, d.TablaObjeto.Registros); operaciones.Add($"Restricción UNIQUE agregada: {nombre}."); return;
            }
            var ck = Regex.Match(resto, @"^\((?<expr>.*)\)$", RegexOptions.Singleline); if (!ck.Success) throw new SemanticaException($"Definición CHECK '{nombre}' no válida.");
            d.TablaObjeto.RestriccionesCheck.Add(ck.Groups["expr"].Value); operaciones.Add($"Restricción CHECK registrada: {nombre}."); return;
        }

        if ((m = Regex.Match(sql, @"^ALTER\s+TABLE\s+(?<tabla>.+?)\s+ADD\s+(?<def>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var destino = ResolverTabla(m.Groups["tabla"].Value.Trim(), esquema);
            var tabla = destino.TablaObjeto;
            var defs = SepararPorComas(m.Groups["def"].Value);
            foreach (var def in defs)
            {
                var cm = Regex.Match(def.Trim(), @"^(?<col>\[[^\]]+\]|""[^""]+""|[\w#$@]+)\s+(?<tipo>[\w]+(?:\s*\([^)]*\))?)", RegexOptions.IgnoreCase);
                if (!cm.Success) throw new SemanticaException($"No se pudo interpretar la nueva columna '{def.Trim()}'.");
                var nombreCol = LimpiarIdentificador(cm.Groups["col"].Value);
                if (tabla.Columnas.Any(c => Igual(c.Nombre, nombreCol))) throw new SemanticaException($"La columna '{nombreCol}' ya existe en la tabla '{tabla.Nombre}'.");
                tabla.Columnas.Add(new ColumnaSimulada { Nombre = nombreCol, Tipo = cm.Groups["tipo"].Value.Trim() });
                foreach (var registro in tabla.Registros) registro.Add("NULL");
                operaciones.Add($"Columna agregada: {tabla.Nombre}.{nombreCol}.");
            }
            return;
        }

        if ((m = Regex.Match(sql, @"^DROP\s+TABLE\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var destino = ResolverTabla(m.Groups[1].Value.Trim(), esquema);
            destino.Base.Tablas.Remove(destino.TablaObjeto);
            operaciones.Add($"Tabla eliminada: {destino.Base.Nombre}.{destino.Esquema}.{destino.Tabla}.");
            return;
        }

        if ((m = Regex.Match(sql, @"^TRUNCATE\s+TABLE\s+(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var destino = ResolverTabla(m.Groups[1].Value.Trim(), esquema);
            var cantidad = destino.TablaObjeto.Registros.Count;
            destino.TablaObjeto.Registros.Clear();
            operaciones.Add($"Tabla '{destino.Tabla}' vaciada ({cantidad} registros eliminados).");
            return;
        }

        if ((m = Regex.Match(sql, @"^INSERT\s+(?:INTO\s+)?(?<tabla>(?:\[[^\]]+\]|[\w#$@]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w#$@]+)){0,3})\s*(?:\((?<cols>.*?)\))?\s+VALUES\s+(?<values>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var destino = ResolverTabla(m.Groups["tabla"].Value.Trim(), esquema);
            var tabla = destino.TablaObjeto;
            var columnasDestino = m.Groups["cols"].Success
                ? SepararPorComas(m.Groups["cols"].Value).Select(x => LimpiarIdentificador(x.Trim())).ToList()
                : tabla.Columnas.Select(c => c.Nombre).ToList();
            if (columnasDestino.Count == 0) throw new SemanticaException("INSERT no especifica columnas ni valores.");
            foreach (var col in columnasDestino)
                if (!tabla.Columnas.Any(c => Igual(c.Nombre, col))) throw new SemanticaException($"La columna '{col}' no existe en la tabla '{tabla.Nombre}'.");
            if (columnasDestino.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columnasDestino.Count) throw new SemanticaException("INSERT contiene columnas duplicadas.");

            var filas = ExtraerFilasValores(m.Groups["values"].Value);
            if (filas.Count == 0) throw new SemanticaException("No se encontraron filas VALUES válidas.");
            foreach (var fila in filas)
            {
                var valores = SepararPorComas(fila);
                if (valores.Count != columnasDestino.Count) throw new SemanticaException($"INSERT intenta insertar {valores.Count} valores, pero especifica {columnasDestino.Count} columnas.");
                var registro = tabla.Columnas.Select(c => c.EsIdentity ? "DEFAULT" : "NULL").ToList();
                for (int i = 0; i < columnasDestino.Count; i++)
                {
                    var indice = tabla.Columnas.FindIndex(c => Igual(c.Nombre, columnasDestino[i]));
                    registro[indice] = NormalizarValor(valores[i]);
                }
                for (int i = 0; i < tabla.Columnas.Count; i++)
                {
                    if (tabla.Columnas[i].EsIdentity && registro[i].Equals("DEFAULT", StringComparison.OrdinalIgnoreCase))
                    {
                        decimal siguiente = tabla.Registros.Select(r => decimal.TryParse(r[i], out var n) ? n : 0m).DefaultIfEmpty(0m).Max() + 1m;
                        registro[i] = siguiente.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                ValidarClavesUnicas(tabla, tabla.Registros.Concat(new[] { registro }).ToList());
                ValidarChecksBasicos(tabla, registro);
                foreach (var fk in tabla.ClavesForaneas) ValidarDatosForaneos(tabla, fk, registro, esquema);
                tabla.Registros.Add(registro);
            }
            operaciones.Add($"Insertados {filas.Count} registro(s) en '{tabla.Nombre}'.");
            return;
        }

        if ((m = Regex.Match(sql, @"^UPDATE\s+(?<tabla>(?:\[[^\]]+\]|[\w#$@]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w#$@]+)){0,3})\s+SET\s+(?<sets>.+?)(?:\s+WHERE\s+(?<where>.+))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var destino = ResolverTabla(m.Groups["tabla"].Value.Trim(), esquema);
            var tabla = destino.TablaObjeto;
            var asignaciones = new List<(int Indice, string Valor)>();
            foreach (var asignacion in SepararPorComas(m.Groups["sets"].Value))
            {
                var am = Regex.Match(asignacion.Trim(), @"^(?<col>\[[^\]]+\]|[\w#$@]+)\s*=\s*(?<valor>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!am.Success) throw new SemanticaException($"Asignación UPDATE no reconocida: '{asignacion}'.");
                var nombreCol = LimpiarIdentificador(am.Groups["col"].Value);
                var indice = tabla.Columnas.FindIndex(c => Igual(c.Nombre, nombreCol));
                if (indice < 0) throw new SemanticaException($"La columna '{nombreCol}' no existe en la tabla '{tabla.Nombre}'.");
                asignaciones.Add((indice, am.Groups["valor"].Value.Trim()));
            }
            var nuevosRegistros = tabla.Registros.Select(r => r.ToList()).ToList();
            int modificados = 0;
            for (int i = 0; i < nuevosRegistros.Count; i++)
            {
                if (m.Groups["where"].Success && !CoincideWhere(m.Groups["where"].Value, tabla, nuevosRegistros[i])) continue;
                foreach (var a in asignaciones) nuevosRegistros[i][a.Indice] = EvaluarValorUpdate(a.Valor, tabla, nuevosRegistros[i]);
                modificados++;
            }
            ValidarClavesUnicas(tabla, nuevosRegistros);
            foreach (var row in nuevosRegistros) { ValidarChecksBasicos(tabla, row); foreach (var fk in tabla.ClavesForaneas) ValidarDatosForaneos(tabla, fk, row, esquema); }
            tabla.Registros = nuevosRegistros;
            operaciones.Add($"UPDATE modificó {modificados} registro(s) de '{tabla.Nombre}'.");
            return;
        }

        if ((m = Regex.Match(sql, @"^DELETE\s+FROM\s+(?<tabla>.+?)(?:\s+WHERE\s+(?<where>.+))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var destino = ResolverTabla(m.Groups["tabla"].Value.Trim(), esquema);
            var tabla = destino.TablaObjeto;
            int antes = tabla.Registros.Count;
            if (m.Groups["where"].Success) tabla.Registros = tabla.Registros.Where(r => !CoincideWhere(m.Groups["where"].Value, tabla, r)).ToList();
            else tabla.Registros.Clear();
            operaciones.Add($"DELETE eliminó {antes - tabla.Registros.Count} registro(s) de '{tabla.Nombre}'.");
            return;
        }

        if ((m = Regex.Match(sql, @"^ALTER\s+TABLE\s+(?<tabla>.+?)\s+ALTER\s+COLUMN\s+(?<col>\[?[^\s\]]+\]?)\s+(?<tipo>[\w]+(?:\s*\([^)]*\))?)(?:\s+(?:NOT\s+NULL|NULL))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var d = ResolverTabla(m.Groups["tabla"].Value.Trim(), esquema);
            var nombre = LimpiarIdentificador(m.Groups["col"].Value.Trim());
            var col = d.TablaObjeto.Columnas.FirstOrDefault(c => Igual(c.Nombre, nombre)) ?? throw new SemanticaException($"La columna '{nombre}' no existe en '{d.Tabla}'.");
            col.Tipo = m.Groups["tipo"].Value.Trim(); operaciones.Add($"Tipo de columna actualizado: {d.Tabla}.{nombre}."); return;
        }

        if ((m = Regex.Match(sql, @"^CREATE\s+(?:NONCLUSTERED\s+|CLUSTERED\s+)?INDEX\s+(?<idx>\w+)\s+ON\s+(?<tabla>[^\s(]+)\s*\((?<cols>[^)]+)\)", RegexOptions.IgnoreCase)).Success)
        {
            var d = ResolverTabla(m.Groups["tabla"].Value.Trim(), esquema); var idx = m.Groups["idx"].Value;
            if (d.TablaObjeto.Indices.Any(x => Igual(x, idx))) throw new SemanticaException($"El índice '{idx}' ya existe.");
            foreach (var c0 in SepararPorComas(m.Groups["cols"].Value)) { var c = LimpiarIdentificador(Regex.Replace(c0.Trim(), @"\s+(ASC|DESC)$", "", RegexOptions.IgnoreCase)); if (!d.TablaObjeto.Columnas.Any(x => Igual(x.Nombre, c))) throw new SemanticaException($"La columna '{c}' no existe para el índice '{idx}'."); }
            d.TablaObjeto.Indices.Add(idx); operaciones.Add($"Índice creado: {idx}."); return;
        }

        if ((m = Regex.Match(sql, @"^DROP\s+INDEX\s+(?<idx>\w+)\s+ON\s+(?<tabla>[^\s;]+)", RegexOptions.IgnoreCase)).Success)
        {
            var d = ResolverTabla(m.Groups["tabla"].Value.Trim(), esquema); var idx = m.Groups["idx"].Value;
            if (!d.TablaObjeto.Indices.RemoveAll(x => Igual(x, idx)).Equals(1)) throw new SemanticaException($"El índice '{idx}' no existe en '{d.Tabla}'.");
            operaciones.Add($"Índice eliminado: {idx}."); return;
        }

        if ((m = Regex.Match(sql, @"^EXEC(?:UTE)?\s+sp_rename\s+(?<args>.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            var args = SepararPorComas(m.Groups["args"].Value).Select(NormalizarValor).ToList();
            if (args.Count < 2) throw new SemanticaException("sp_rename requiere el nombre actual y el nuevo nombre.");
            var actual = args[0]; var nuevo = args[1]; var tipo = args.Count > 2 ? args[2] : "OBJECT";
            if (tipo.Equals("COLUMN", StringComparison.OrdinalIgnoreCase))
            {
                var p = actual.Split('.'); if (p.Length < 2) throw new SemanticaException("Nombre de columna inválido para sp_rename.");
                var d = ResolverTabla(string.Join('.', p.Take(p.Length - 1)), esquema); var viejo = LimpiarIdentificador(p[^1]);
                var c = d.TablaObjeto.Columnas.FirstOrDefault(x => Igual(x.Nombre, viejo)) ?? throw new SemanticaException($"La columna '{viejo}' no existe.");
                if (d.TablaObjeto.Columnas.Any(x => Igual(x.Nombre, nuevo))) throw new SemanticaException($"La columna '{nuevo}' ya existe.");
                c.Nombre = nuevo; operaciones.Add($"Columna renombrada: {viejo} -> {nuevo}."); return;
            }
            var partes = SepararNombreCalificado(actual).Select(LimpiarIdentificador).ToList();
            if (partes.Count >= 2)
            {
                var d = ResolverTabla(actual, esquema); if (d.Base.Tablas.Any(t => Igual(t.Nombre, nuevo) && Igual(t.Esquema, d.Esquema))) throw new SemanticaException($"La tabla '{nuevo}' ya existe.");
                d.TablaObjeto.Nombre = nuevo; operaciones.Add($"Tabla renombrada: {d.Tabla} -> {nuevo}."); return;
            }
            throw new SemanticaException("No se pudo resolver el objeto que se desea renombrar.");
        }

        if (Regex.IsMatch(sql, @"^(SELECT|BEGIN|COMMIT|ROLLBACK|DECLARE|SET|PRINT|GRANT|REVOKE|DENY)\b", RegexOptions.IgnoreCase))
        {
            // Son instrucciones sintácticamente aceptadas por el analizador, pero no todas alteran el esquema simulado.
            if (Regex.IsMatch(sql, @"^SELECT\b", RegexOptions.IgnoreCase)) ValidarSelectBasico(sql, esquema);
            operaciones.Add($"Instrucción reconocida sin cambios directos en el esquema: {PrimeraPalabra(sql)}.");
            return;
        }

        throw new SemanticaException($"La instrucción '{PrimeraPalabra(sql)}' todavía no está implementada en el simulador; no se puede garantizar su validación semántica.");
    }

    private static void ValidarDefinicionForanea(TablaSimulada tabla, ClaveForaneaSimulada fk, EsquemaSimulado esquema)
    {
        foreach (var c in fk.Columnas) if (!tabla.Columnas.Any(x => Igual(x.Nombre, c))) throw new SemanticaException($"La clave foránea '{fk.Nombre}' usa la columna inexistente '{c}'.");
        var refTabla = ResolverTabla(fk.TablaReferenciada, esquema).TablaObjeto;
        foreach (var c in fk.ColumnasReferenciadas) if (!refTabla.Columnas.Any(x => Igual(x.Nombre, c))) throw new SemanticaException($"La clave foránea '{fk.Nombre}' referencia la columna inexistente '{c}'.");
        if (fk.Columnas.Count != fk.ColumnasReferenciadas.Count) throw new SemanticaException($"La clave foránea '{fk.Nombre}' tiene distinta cantidad de columnas origen y destino.");
    }
    private static void ValidarDatosForaneos(TablaSimulada tabla, ClaveForaneaSimulada fk, List<string> fila, EsquemaSimulado esquema)
    {
        var refTabla = ResolverTabla(fk.TablaReferenciada, esquema).TablaObjeto;
        var vals = fk.Columnas.Select(c => fila[tabla.Columnas.FindIndex(x => Igual(x.Nombre, c))]).ToList();
        if (vals.Any(v => v.Equals("NULL", StringComparison.OrdinalIgnoreCase))) return;
        bool existe = refTabla.Registros.Any(r => fk.ColumnasReferenciadas.Select((c, i) => (c, i)).All(x => Igual(r[refTabla.Columnas.FindIndex(col => Igual(col.Nombre, x.c))], vals[x.i])));
        if (!existe) throw new SemanticaException($"Violación de clave foránea '{fk.Nombre}': no existe el registro relacionado en '{fk.TablaReferenciada}' para ({string.Join(", ", vals)}).");
    }
    private static void ValidarChecksBasicos(TablaSimulada tabla, List<string> fila)
    {
        foreach (var check in tabla.RestriccionesCheck)
        {
            var m = Regex.Match(check, @"^\s*(?<col>\w+)\s*(?<op>>=|<=|<>|!=|=|>|<)\s*(?<val>-?\d+(?:\.\d+)?|'[^']*')\s*$", RegexOptions.IgnoreCase); if (!m.Success) continue;
            int ix = tabla.Columnas.FindIndex(c => Igual(c.Nombre, m.Groups["col"].Value)); if (ix < 0) continue; if (fila[ix] == "NULL") continue;
            var val = NormalizarValor(m.Groups["val"].Value); int cmp = decimal.TryParse(fila[ix], out var a) && decimal.TryParse(val, out var b) ? a.CompareTo(b) : string.Compare(fila[ix], val, StringComparison.OrdinalIgnoreCase);
            bool ok = m.Groups["op"].Value switch { ">" => cmp > 0, ">=" => cmp >= 0, "<" => cmp < 0, "<=" => cmp <= 0, "=" => cmp == 0, "<>" or "!=" => cmp != 0, _ => true }; if (!ok) throw new SemanticaException($"Violación de CHECK en '{tabla.Nombre}': {check}.");
        }
    }

    private static string EvaluarValorUpdate(string expresion, TablaSimulada tabla, List<string> registro)
    {
        var v = expresion.Trim();
        var m = Regex.Match(v, @"^(?<col>\w+)\s*(?<op>[+\-*/])\s*(?<num>-?\d+(?:\.\d+)?)$");
        if (!m.Success) return NormalizarValor(v);
        int ix = tabla.Columnas.FindIndex(c => Igual(c.Nombre, m.Groups["col"].Value));
        if (ix < 0) throw new SemanticaException($"La columna '{m.Groups["col"].Value}' no existe en la expresión UPDATE.");
        if (!decimal.TryParse(registro[ix], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var a) || !decimal.TryParse(m.Groups["num"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var b)) throw new SemanticaException($"La expresión UPDATE '{v}' requiere valores numéricos.");
        decimal r = m.Groups["op"].Value switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" when b != 0 => a / b, "/" => throw new SemanticaException("División entre cero en UPDATE."), _ => a };
        return r.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ValidarClavesUnicas(TablaSimulada tabla, List<List<string>> registros)
    {
        foreach (var clave in tabla.ClavesUnicas)
        {
            var indices = clave.Select(k => tabla.Columnas.FindIndex(c => Igual(c.Nombre, k))).ToList();
            var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var registro in registros)
            {
                var valores = indices.Select(ix => registro[ix]).ToList();
                if (valores.Any(v => v.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)))
                    throw new SemanticaException($"No se puede validar la clave única ({string.Join(", ", clave)}) porque contiene DEFAULT sin un valor simulado.");
                var claveCompuesta = string.Join("\u001f", valores);
                if (!vistos.Add(claveCompuesta))
                    throw new SemanticaException($"Registro duplicado: la clave ({string.Join(", ", clave)}) se repite en la tabla '{tabla.Nombre}'.");
            }
        }
    }

    private static bool CoincideWhere(string condicion, TablaSimulada tabla, List<string> registro)
    {
        var condiciones = Regex.Split(condicion, @"\s+AND\s+", RegexOptions.IgnoreCase);
        foreach (var expresion in condiciones)
        {
            var inMatch = Regex.Match(expresion.Trim(), @"^(?<col>(?:\[[^\]]+\]|[\w#$@]+)(?:\.(?:\[[^\]]+\]|[\w#$@]+))?)\s+(?<not>NOT\s+)?IN\s*\((?<vals>[^)]*)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (inMatch.Success)
            {
                var nombre = LimpiarIdentificador(inMatch.Groups["col"].Value.Split('.').Last());
                var ix = tabla.Columnas.FindIndex(c => Igual(c.Nombre, nombre));
                if (ix < 0) throw new SemanticaException($"La columna '{nombre}' de WHERE no existe en la tabla '{tabla.Nombre}'.");
                var opciones = SepararPorComas(inMatch.Groups["vals"].Value).Select(NormalizarValor).ToList();
                bool contiene = opciones.Any(v => Igual(v, registro[ix]));
                if (inMatch.Groups["not"].Success ? contiene : !contiene) return false;
                continue;
            }
            var m = Regex.Match(expresion.Trim(), @"^(?<col>(?:\[[^\]]+\]|[\w#$@]+)(?:\.(?:\[[^\]]+\]|[\w#$@]+))?)\s*(?<op>IS\s+NOT\s+NULL|IS\s+NULL|>=|<=|<>|!=|=|>|<)\s*(?<valor>.+)?$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) throw new SemanticaException($"Condición WHERE no soportada por el simulador: '{expresion.Trim()}'.");
            var nombreCol = LimpiarIdentificador(m.Groups["col"].Value.Split('.').Last());
            var indice = tabla.Columnas.FindIndex(c => Igual(c.Nombre, nombreCol));
            if (indice < 0) throw new SemanticaException($"La columna '{nombreCol}' de WHERE no existe en la tabla '{tabla.Nombre}'.");
            var actual = registro[indice];
            var op = Regex.Replace(m.Groups["op"].Value.ToUpperInvariant(), @"\s+", " ");
            if (op == "IS NULL") { if (!actual.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return false; continue; }
            if (op == "IS NOT NULL") { if (actual.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return false; continue; }
            var esperado = NormalizarValor(m.Groups["valor"].Value);
            int comparacion;
            if (decimal.TryParse(actual, out var numeroActual) && decimal.TryParse(esperado, out var numeroEsperado)) comparacion = numeroActual.CompareTo(numeroEsperado);
            else comparacion = string.Compare(actual, esperado, StringComparison.OrdinalIgnoreCase);
            bool cumple = op switch { "=" => comparacion == 0, "!=" or "<>" => comparacion != 0, ">" => comparacion > 0, "<" => comparacion < 0, ">=" => comparacion >= 0, "<=" => comparacion <= 0, _ => false };
            if (!cumple) return false;
        }
        return true;
    }

    private static void ValidarSelectBasico(string sql, EsquemaSimulado esquema)
    {
        // Valida las tablas mencionadas en FROM/JOIN y columnas calificadas por alias.
        // La simulación no calcula el conjunto de resultados de SELECT.
        var tablas = new Dictionary<string, TablaSimulada>(StringComparer.OrdinalIgnoreCase);
        var fromMatches = Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+(?<tabla>(?:\[[^\]]+\]|[\w#$@]+)(?:\s*\.\s*(?:\[[^\]]+\]|[\w#$@]+)){0,3})(?:\s+(?:AS\s+)?(?<alias>[A-Za-z_][\w$#@]*))?", RegexOptions.IgnoreCase);
        foreach (Match tm in fromMatches)
        {
            var nombre = tm.Groups["tabla"].Value.Trim();
            var destino = ResolverTabla(nombre, esquema);
            tablas[destino.Tabla] = destino.TablaObjeto;
            if (tm.Groups["alias"].Success && !new[] { "ON", "WHERE", "INNER", "LEFT", "RIGHT", "FULL", "JOIN", "GROUP", "ORDER", "CROSS" }.Contains(tm.Groups["alias"].Value, StringComparer.OrdinalIgnoreCase))
                tablas[tm.Groups["alias"].Value] = destino.TablaObjeto;
        }
        if (fromMatches.Count == 0)
        {
            if (Regex.IsMatch(sql, @"\bFROM\b", RegexOptions.IgnoreCase))
                throw new SemanticaException("No se pudieron identificar las tablas del SELECT.");
            return;
        }
        // Validar referencias alias.columna. No se intenta evaluar funciones ni resultados.
        foreach (Match cm in Regex.Matches(sql, @"(?<![\w])(?<alias>[A-Za-z_][\w$#@]*)\.(?<col>[A-Za-z_][\w$#@]*)", RegexOptions.IgnoreCase))
        {
            var alias = cm.Groups["alias"].Value;
            var col = cm.Groups["col"].Value;
            if (tablas.TryGetValue(alias, out var tabla) && !tabla.Columnas.Any(c => Igual(c.Nombre, col)))
                throw new SemanticaException($"La columna '{col}' no existe en la tabla '{tabla.Nombre}' (alias '{alias}').");
        }
        // Validar columnas sin alias solo cuando son referencias simples en la lista SELECT.
        var lista = Regex.Match(sql, @"^\s*SELECT\s+(?<cols>.+?)\s+FROM\b", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups["cols"].Value;
        if (lista.Length > 0 && lista.Trim() != "*")
        {
            foreach (var item in SepararPorComas(lista))
            {
                var expr = Regex.Replace(item.Trim(), @"\s+AS\s+.+$", "", RegexOptions.IgnoreCase);
                if (Regex.IsMatch(expr, @"^(COUNT|SUM|AVG|MIN|MAX)\s*\(", RegexOptions.IgnoreCase))
                {
                    var inner = Regex.Match(expr, @"\(\s*(?:DISTINCT\s+)?(?<col>[A-Za-z_][\w$#@]*(?:\.[A-Za-z_][\w$#@]*)?)\s*\)", RegexOptions.IgnoreCase);
                    if (inner.Success && inner.Groups["col"].Value != "*") ValidarColumnaSelect(inner.Groups["col"].Value, tablas);
                    continue;
                }
                if (Regex.IsMatch(expr, @"^[A-Za-z_][\w$#@]*(?:\.[A-Za-z_][\w$#@]*)?$")) ValidarColumnaSelect(expr, tablas);
            }
        }
    }

    private static void ValidarColumnaSelect(string referencia, Dictionary<string, TablaSimulada> tablas)
    {
        var partes = referencia.Split('.');
        var columna = partes[^1];
        if (partes.Length == 2 && tablas.TryGetValue(partes[0], out var tablaAlias))
        {
            if (!tablaAlias.Columnas.Any(c => Igual(c.Nombre, columna))) throw new SemanticaException($"La columna '{columna}' no existe en la tabla '{tablaAlias.Nombre}'.");
            return;
        }
        var coincidencias = tablas.Values.Distinct().Where(t => t.Columnas.Any(c => Igual(c.Nombre, columna))).ToList();
        if (coincidencias.Count == 0) throw new SemanticaException($"La columna '{columna}' no existe en las tablas consultadas.");
    }

    private static string ResolverIfDbId(string codigo, EsquemaSimulado esquema)
    {
        // Soporta el patrón habitual IF DB_ID('nombre') IS [NOT] NULL BEGIN ... END.
        var patron = @"IF\s+DB_ID\s*\(\s*'(?<db>[^']+)'\s*\)\s+IS\s+(?<op>NOT\s+NULL|NULL)\s+BEGIN(?<body>.*?)\bEND\s*;?";
        return Regex.Replace(codigo, patron, m =>
        {
            var existe = BuscarBase(esquema, m.Groups["db"].Value) != null;
            var condicion = m.Groups["op"].Value.Trim().Equals("NOT NULL", StringComparison.OrdinalIgnoreCase) ? existe : !existe;
            return condicion ? m.Groups["body"].Value : "";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    // Resuelve el nombre de destino de CREATE TABLE sin exigir que la tabla exista todavía.
    private static (BaseDatosSimulada Base, string Esquema, string Tabla) ResolverDestinoCreacion(string nombre, EsquemaSimulado esquema)
    {
        var partes = SepararNombreCalificado(nombre).Select(LimpiarIdentificador).ToList();
        string bdNombre, schemaNombre, tablaNombre;
        if (partes.Count >= 3)
        {
            bdNombre = partes[^3]; schemaNombre = partes[^2]; tablaNombre = partes[^1];
        }
        else if (partes.Count == 2)
        {
            bdNombre = esquema.BaseDatosActual ?? throw new SemanticaException($"No hay una base de datos seleccionada. Ejecuta USE antes de crear '{nombre}'.");
            schemaNombre = partes[0]; tablaNombre = partes[1];
        }
        else if (partes.Count == 1)
        {
            bdNombre = esquema.BaseDatosActual ?? throw new SemanticaException($"No hay una base de datos seleccionada. Ejecuta USE antes de crear '{nombre}'.");
            schemaNombre = "dbo"; tablaNombre = partes[0];
        }
        else throw new SemanticaException($"Nombre de tabla no válido: '{nombre}'.");

        var bd = BuscarBase(esquema, bdNombre) ?? throw new SemanticaException($"La base de datos '{bdNombre}' no existe.");
        return (bd, schemaNombre, tablaNombre);
    }

    private static (BaseDatosSimulada Base, string Esquema, string Tabla, TablaSimulada TablaObjeto) ResolverTabla(string nombre, EsquemaSimulado esquema)
    {
        var partes = SepararNombreCalificado(nombre).Select(LimpiarIdentificador).ToList();
        string bdNombre, schemaNombre, tablaNombre;
        if (partes.Count >= 3) { bdNombre = partes[^3]; schemaNombre = partes[^2]; tablaNombre = partes[^1]; }
        else if (partes.Count == 2) { bdNombre = esquema.BaseDatosActual ?? throw new SemanticaException($"No hay una base de datos seleccionada. Ejecuta USE o especifica la base de datos para '{nombre}'."); schemaNombre = partes[0]; tablaNombre = partes[1]; }
        else { bdNombre = esquema.BaseDatosActual ?? throw new SemanticaException($"No hay una base de datos seleccionada. Ejecuta USE o especifica la base de datos para '{nombre}'."); schemaNombre = "dbo"; tablaNombre = partes[0]; }
        var bd = BuscarBase(esquema, bdNombre) ?? throw new SemanticaException($"La base de datos '{bdNombre}' no existe.");
        var tabla = bd.Tablas.FirstOrDefault(t => Igual(t.Nombre, tablaNombre) && Igual(t.Esquema, schemaNombre));
        if (tabla == null) throw new SemanticaException($"La tabla '{schemaNombre}.{tablaNombre}' no existe en la base de datos '{bd.Nombre}'.");
        return (bd, schemaNombre, tablaNombre, tabla);
    }

    private static bool MismoNombreTabla(TablaSimulada t, string schema, string nombre) => Igual(t.Esquema, schema) && Igual(t.Nombre, nombre);
    private static BaseDatosSimulada? BuscarBase(EsquemaSimulado e, string nombre) => e.BasesDatos.FirstOrDefault(b => Igual(b.Nombre, nombre));
    private static bool Igual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string PrimeraPalabra(string sql) => Regex.Match(sql.Trim(), @"^[\w]+").Value.ToUpperInvariant();
    private static string LimpiarIdentificador(string valor)
    {
        valor = valor.Trim();
        if (valor.StartsWith("[") && valor.EndsWith("]")) return valor[1..^1].Replace("]]", "]");
        if (valor.StartsWith("\"") && valor.EndsWith("\"")) return valor[1..^1].Replace("\"\"", "\"");
        return valor;
    }

    private static List<string> SepararNombreCalificado(string nombre)
    {
        var partes = new List<string>(); var actual = new StringBuilder(); bool corchete = false, comilla = false;
        foreach (char c in nombre.Trim())
        {
            if (c == '[' && !comilla) corchete = true;
            if (c == ']' && !comilla) corchete = false;
            if (c == '"' && !corchete) comilla = !comilla;
            if (c == '.' && !corchete && !comilla) { partes.Add(actual.ToString().Trim()); actual.Clear(); }
            else actual.Append(c);
        }
        if (actual.Length > 0) partes.Add(actual.ToString().Trim());
        return partes;
    }

    private static List<string> SepararPorComas(string texto)
    {
        var resultado = new List<string>(); var actual = new StringBuilder(); int profundidad = 0; bool cadena = false, corchete = false;
        for (int i = 0; i < texto.Length; i++)
        {
            char c = texto[i];
            if (c == '\'' && !corchete)
            {
                actual.Append(c);
                if (cadena && i + 1 < texto.Length && texto[i + 1] == '\'') { actual.Append(texto[++i]); continue; }
                cadena = !cadena; continue;
            }
            if (!cadena)
            {
                if (c == '[') corchete = true; else if (c == ']') corchete = false;
                if (!corchete) { if (c == '(') profundidad++; else if (c == ')') profundidad--; }
                if (c == ',' && profundidad == 0 && !corchete) { resultado.Add(actual.ToString().Trim()); actual.Clear(); continue; }
            }
            actual.Append(c);
        }
        if (actual.Length > 0) resultado.Add(actual.ToString().Trim());
        return resultado;
    }

    private static List<string> ExtraerFilasValores(string texto)
    {
        var filas = new List<string>(); int i = 0;
        while (i < texto.Length)
        {
            while (i < texto.Length && (char.IsWhiteSpace(texto[i]) || texto[i] == ',')) i++;
            if (i >= texto.Length) break;
            if (texto[i] != '(') throw new SemanticaException("Formato VALUES no reconocido. Se esperaba una fila entre paréntesis.");
            int inicio = ++i, profundidad = 0; bool cadena = false;
            for (; i < texto.Length; i++)
            {
                char c = texto[i];
                if (c == '\'') { if (cadena && i + 1 < texto.Length && texto[i + 1] == '\'') { i++; continue; } cadena = !cadena; }
                else if (!cadena) { if (c == '(') profundidad++; else if (c == ')') { if (profundidad == 0) break; profundidad--; } }
            }
            if (i >= texto.Length) throw new SemanticaException("Fila VALUES sin paréntesis de cierre.");
            filas.Add(texto[inicio..i]); i++;
        }
        return filas;
    }

    private static string NormalizarValor(string valor)
    {
        valor = valor.Trim();
        if (valor.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return "NULL";
        if (valor.StartsWith("N'", StringComparison.OrdinalIgnoreCase) && valor.EndsWith("'")) return valor[2..^1].Replace("''", "'");
        if (valor.StartsWith("'") && valor.EndsWith("'")) return valor[1..^1].Replace("''", "'");
        return valor;
    }

    private static List<(string Texto, int Linea)> SepararSentencias(string codigo)
    {
        // En SQL Server, GO separa lotes aunque no lleve punto y coma.
        codigo = Regex.Replace(codigo, @"(?im)^[ \t]*GO[ \t]*(?:--.*)?$", ";", RegexOptions.IgnoreCase);
        // También admite scripts sencillos que separan instrucciones por líneas sin usar ;.
        // No separar por SET: en UPDATE multilínea, SET inicia la cláusula de asignaciones y no una sentencia independiente.
        codigo = Regex.Replace(codigo, @"(?im)(?=^[ \t]*(?:CREATE[ \t]+DATABASE|USE|DROP[ \t]+DATABASE|CREATE[ \t]+TABLE|CREATE[ \t]+(?:NONCLUSTERED[ \t]+|CLUSTERED[ \t]+)?INDEX|ALTER[ \t]+TABLE|DROP[ \t]+(?:TABLE|INDEX)|TRUNCATE[ \t]+TABLE|EXEC(?:UTE)?[ \t]+sp_rename|INSERT(?:[ \t]+INTO)?|UPDATE|DELETE[ \t]+FROM|SELECT|BEGIN[ \t]+TRAN|COMMIT|ROLLBACK|DECLARE|PRINT)\b)", ";");
        var salida = new List<(string, int)>(); var actual = new StringBuilder(); int linea = 1, inicioLinea = 1; bool cadena = false, comentarioLinea = false, comentarioBloque = false, corchete = false;
        for (int i = 0; i < codigo.Length; i++)
        {
            char c = codigo[i], siguiente = i + 1 < codigo.Length ? codigo[i + 1] : '\0';
            if (c == '\n') { linea++; if (actual.ToString().Trim().Length == 0) inicioLinea = linea; comentarioLinea = false; actual.Append(c); continue; }
            if (comentarioLinea) continue;
            if (comentarioBloque) { if (c == '*' && siguiente == '/') { comentarioBloque = false; i++; } continue; }
            if (!cadena && !corchete && c == '-' && siguiente == '-') { comentarioLinea = true; i++; continue; }
            if (!cadena && !corchete && c == '/' && siguiente == '*') { comentarioBloque = true; i++; continue; }
            if (c == '\'' && !corchete)
            {
                actual.Append(c);
                if (cadena && siguiente == '\'') { actual.Append(codigo[++i]); continue; }
                cadena = !cadena; continue;
            }
            if (!cadena) { if (c == '[') corchete = true; else if (c == ']') corchete = false; }
            if (c == ';' && !cadena && !corchete) { if (actual.ToString().Trim().Length > 0) salida.Add((actual.ToString().Trim(), inicioLinea)); actual.Clear(); inicioLinea = linea; }
            else actual.Append(c);
        }
        if (actual.ToString().Trim().Length > 0) salida.Add((actual.ToString().Trim(), inicioLinea));
        // GO es separador de lote, no una instrucción ejecutable.
        return salida.Select(x => (Regex.Replace(x.Item1, @"(?m)^\s*GO\s*$", "", RegexOptions.IgnoreCase).Trim(), x.Item2)).ToList();
    }

    private sealed class SemanticaException : Exception { public SemanticaException(string mensaje) : base(mensaje) { } }
}
