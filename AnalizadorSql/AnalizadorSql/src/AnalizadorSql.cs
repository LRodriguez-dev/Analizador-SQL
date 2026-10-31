
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AnalizadorSql.src
{
    public class ResultadoAnalisis
    {
        public bool EsValido { get; set; }

        public List<TokenSql> Tokens { get; set; } = new();

        public List<ErrorSql> Errores { get; set; } = new();

        public List<ErrorSql> ErroresLexicos { get; set; } = new();

        public List<ErrorSql> ErroresSintacticos { get; set; } = new();
    }

    public class TokenSql
    {
        public string Tipo { get; set; } = "";
        public string Texto { get; set; } = "";
        public int Linea { get; set; }
        public int Columna { get; set; }
    }

    public class ErrorSql
    {
        public string Tipo { get; set; } = "";
        public int Linea { get; set; }
        public int Columna { get; set; }
        public string Mensaje { get; set; } = "";
    }

    public class AnalizadorSql
    {
        public ResultadoAnalisis Analizar(string codigo)
        {
            var resultado = new ResultadoAnalisis();

            if (string.IsNullOrWhiteSpace(codigo))
            {
                resultado.ErroresSintacticos.Add(new ErrorSql
                {
                    Tipo = "Entrada",
                    Linea = 1,
                    Columna = 1,
                    Mensaje = "No se proporcionó código SQL."
                });

                ActualizarResultado(resultado);
                return resultado;
            }

            // Obtener tokens del documento completo.
            var parser = new TSql160Parser(true);

            var tokens = parser.GetTokenStream(
                new StringReader(codigo),
                out var erroresLexicos
            );

            foreach (var token in tokens)
            {
                resultado.Tokens.Add(new TokenSql
                {
                    Tipo = token.TokenType.ToString(),
                    Texto = token.Text,
                    Linea = token.Line,
                    Columna = token.Column
                });
            }

            AgregarErrores(
                resultado.ErroresLexicos,
                erroresLexicos,
                "Léxico",
                0
            );

            // Identificar bloques de prueba.
            var bloques = ObtenerBloques(codigo);

            foreach (var bloque in bloques)
            {
                var parserBloque = new TSql160Parser(true);

                parserBloque.Parse(
                    new StringReader(bloque.Codigo),
                    out var erroresSintacticos
                );

                AgregarErrores(
                    resultado.ErroresSintacticos,
                    erroresSintacticos,
                    "Sintáctico",
                    bloque.LineaInicial - 1
                );
            }

            ActualizarResultado(resultado);

            return resultado;
        }

        private static void AgregarErrores(
            List<ErrorSql> destino,
            IList<ParseError> errores,
            string tipo,
            int desplazamientoLineas)
        {
            foreach (var error in errores)
            {
                destino.Add(new ErrorSql
                {
                    Tipo = tipo,
                    Linea = error.Line + desplazamientoLineas,
                    Columna = error.Column,
                    Mensaje = error.Message
                });
            }
        }

        private static List<BloqueSql> ObtenerBloques(
            string codigo)
        {
            var bloques = new List<BloqueSql>();

            var lineas = Regex.Split(
                codigo,
                @"\r\n|\n|\r"
            );

            // Separadores específicos de los scripts
            // de prueba: -- BLOQUE 1, -- BLOQUE 2...
            var separador = new Regex(
                @"^\s*--\s*BLOQUE\s+\d+\b",
                RegexOptions.IgnoreCase
            );

            int inicio = 0;
            bool encontroSeparadores = false;

            for (int i = 0; i < lineas.Length; i++)
            {
                if (!separador.IsMatch(lineas[i]))
                    continue;

                encontroSeparadores = true;

                if (i > inicio)
                {
                    bloques.Add(new BloqueSql
                    {
                        LineaInicial = inicio + 1,
                        Codigo = string.Join(
                            Environment.NewLine,
                            lineas.Skip(inicio).Take(i - inicio)
                        )
                    });
                }

                inicio = i;
            }

            if (inicio < lineas.Length)
            {
                bloques.Add(new BloqueSql
                {
                    LineaInicial = inicio + 1,
                    Codigo = string.Join(
                        Environment.NewLine,
                        lineas.Skip(inicio)
                    )
                });
            }

            // Si no hay separadores, analizar
            // todo el script como una unidad.
            if (!encontroSeparadores)
            {
                return new List<BloqueSql>
                {
                    new BloqueSql
                    {
                        LineaInicial = 1,
                        Codigo = codigo
                    }
                };
            }

            return bloques;
        }

        private static void ActualizarResultado(
            ResultadoAnalisis resultado)
        {
            resultado.ErroresLexicos =
                resultado.ErroresLexicos
                    .DistinctBy(e => (
                        e.Tipo,
                        e.Linea,
                        e.Columna,
                        e.Mensaje
                    ))
                    .OrderBy(e => e.Linea)
                    .ThenBy(e => e.Columna)
                    .ToList();

            resultado.ErroresSintacticos =
                resultado.ErroresSintacticos
                    .DistinctBy(e => (
                        e.Tipo,
                        e.Linea,
                        e.Columna,
                        e.Mensaje
                    ))
                    .OrderBy(e => e.Linea)
                    .ThenBy(e => e.Columna)
                    .ToList();

            resultado.Errores =
                resultado.ErroresLexicos
                    .Concat(resultado.ErroresSintacticos)
                    .DistinctBy(e => (
                        e.Linea,
                        e.Columna,
                        e.Mensaje
                    ))
                    .OrderBy(e => e.Linea)
                    .ThenBy(e => e.Columna)
                    .ToList();

            resultado.EsValido =
                resultado.Errores.Count == 0;
        }

        private class BloqueSql
        {
            public int LineaInicial { get; set; }
            public string Codigo { get; set; } = "";
        }
    }
}
