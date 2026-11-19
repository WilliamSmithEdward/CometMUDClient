using System.Globalization;

namespace Comet.Core.Scripting;

/// <summary>
/// Evaluates #math and #if expressions.
/// </summary>
/// <remarks>
/// Operators by rising precedence: || , &amp;&amp; , == = != , &lt; &gt; &lt;= &gt;= , + - , * / % , unary ! - +.
/// Numbers are integers unless written with a decimal point; integer division truncates.
/// Strings are written "like this" or [like this] and compare ordinally. T is 1 and F is 0.
/// </remarks>
public static class ExpressionEvaluator
{
    private const int MaxDepth = 64;

    public static bool TryEvaluate(string expression, out string result, out string? error)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var parser = new Parser(expression);
        try
        {
            Value value = parser.ParseOr(0);
            parser.SkipSpaces();
            if (!parser.AtEnd)
            {
                throw new FormatException($"Unexpected '{parser.Current}' at position {parser.Position + 1}.");
            }
            result = value.ToString();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or DivideByZeroException or OverflowException)
        {
            result = "0";
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Evaluates a condition. An invalid expression is false.</summary>
    public static bool IsTrue(string expression, out string? error)
    {
        return TryEvaluate(expression, out string result, out error)
            && result.Length > 0
            && !(double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number == 0);
    }

    private readonly struct Value
    {
        private readonly string? _text;
        private readonly double _number;
        private readonly bool _integer;

        private Value(string? text, double number, bool integer)
        {
            _text = text;
            _number = number;
            _integer = integer;
        }

        public static Value Text(string text) => new(text, 0, false);
        public static Value Number(double number, bool integer) => new(null, number, integer);
        public static Value Boolean(bool value) => new(null, value ? 1 : 0, true);

        public bool IsText => _text is not null;
        public bool IsInteger => _integer;
        public bool IsTruthy => _text is not null ? _text.Length > 0 : _number != 0;

        public double AsNumber()
        {
            if (_text is null)
            {
                return _number;
            }
            if (double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return parsed;
            }
            throw new FormatException($"'{_text}' is not a number.");
        }

        public override string ToString()
        {
            if (_text is not null)
            {
                return _text;
            }
            return _integer
                ? ((long)_number).ToString(CultureInfo.InvariantCulture)
                : _number.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }

    private ref struct Parser(string text)
    {
        private readonly ReadOnlySpan<char> _text = text;
        private int _position;

        public readonly int Position => _position;
        public readonly bool AtEnd => _position >= _text.Length;
        public readonly char Current => _text[_position];

        public void SkipSpaces()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
            {
                _position++;
            }
        }

        private bool Take(string op)
        {
            SkipSpaces();
            if (_text[_position..].StartsWith(op, StringComparison.Ordinal))
            {
                _position += op.Length;
                return true;
            }
            return false;
        }

        private readonly bool Peek(string op) => _text[_position..].StartsWith(op, StringComparison.Ordinal);

        public Value ParseOr(int depth)
        {
            Value left = ParseAnd(depth);
            while (Take("||") || Take("|"))
            {
                Value right = ParseAnd(depth);
                left = Value.Boolean(left.IsTruthy || right.IsTruthy);
            }
            return left;
        }

        private Value ParseAnd(int depth)
        {
            Value left = ParseEquality(depth);
            while (Take("&&") || Take("&"))
            {
                Value right = ParseEquality(depth);
                left = Value.Boolean(left.IsTruthy && right.IsTruthy);
            }
            return left;
        }

        private Value ParseEquality(int depth)
        {
            Value left = ParseRelational(depth);
            while (true)
            {
                if (Take("!="))
                {
                    left = Value.Boolean(Compare(left, ParseRelational(depth)) != 0);
                }
                else if (Take("==") || Take("="))
                {
                    left = Value.Boolean(Compare(left, ParseRelational(depth)) == 0);
                }
                else
                {
                    return left;
                }
            }
        }

        private Value ParseRelational(int depth)
        {
            Value left = ParseAdditive(depth);
            while (true)
            {
                if (Take("<="))
                {
                    left = Value.Boolean(Compare(left, ParseAdditive(depth)) <= 0);
                }
                else if (Take(">="))
                {
                    left = Value.Boolean(Compare(left, ParseAdditive(depth)) >= 0);
                }
                else if (Take("<"))
                {
                    left = Value.Boolean(Compare(left, ParseAdditive(depth)) < 0);
                }
                else if (Take(">"))
                {
                    left = Value.Boolean(Compare(left, ParseAdditive(depth)) > 0);
                }
                else
                {
                    return left;
                }
            }
        }

        private Value ParseAdditive(int depth)
        {
            Value left = ParseMultiplicative(depth);
            while (true)
            {
                if (Take("+"))
                {
                    Value right = ParseMultiplicative(depth);
                    left = Value.Number(left.AsNumber() + right.AsNumber(), left.IsInteger && right.IsInteger);
                }
                else if (Take("-"))
                {
                    Value right = ParseMultiplicative(depth);
                    left = Value.Number(left.AsNumber() - right.AsNumber(), left.IsInteger && right.IsInteger);
                }
                else
                {
                    return left;
                }
            }
        }

        private Value ParseMultiplicative(int depth)
        {
            Value left = ParseUnary(depth);
            while (true)
            {
                SkipSpaces();
                if (Take("*"))
                {
                    Value right = ParseUnary(depth);
                    left = Value.Number(left.AsNumber() * right.AsNumber(), left.IsInteger && right.IsInteger);
                }
                else if (Take("/"))
                {
                    Value right = ParseUnary(depth);
                    bool integer = left.IsInteger && right.IsInteger;
                    double divisor = right.AsNumber();
                    if (divisor == 0)
                    {
                        throw new DivideByZeroException("Division by zero.");
                    }
                    double quotient = left.AsNumber() / divisor;
                    left = Value.Number(integer ? Math.Truncate(quotient) : quotient, integer);
                }
                else if (Take("%"))
                {
                    Value right = ParseUnary(depth);
                    double divisor = right.AsNumber();
                    if (divisor == 0)
                    {
                        throw new DivideByZeroException("Division by zero.");
                    }
                    left = Value.Number(left.AsNumber() % divisor, left.IsInteger && right.IsInteger);
                }
                else
                {
                    return left;
                }
            }
        }

        private Value ParseUnary(int depth)
        {
            SkipSpaces();
            if (!AtEnd && Current == '!' && !Peek("!="))
            {
                _position++;
                return Value.Boolean(!ParseUnary(depth).IsTruthy);
            }
            if (Take("-"))
            {
                Value operand = ParseUnary(depth);
                return Value.Number(-operand.AsNumber(), operand.IsInteger);
            }
            if (Take("+"))
            {
                return ParseUnary(depth);
            }
            return ParsePrimary(depth);
        }

        private Value ParsePrimary(int depth)
        {
            SkipSpaces();
            if (AtEnd)
            {
                throw new FormatException("Expression ended early.");
            }

            char c = Current;
            if (c == '(')
            {
                if (depth >= MaxDepth)
                {
                    throw new FormatException("Expression is nested too deeply.");
                }
                _position++;
                Value inner = ParseOr(depth + 1);
                if (!Take(")"))
                {
                    throw new FormatException("Missing ')'.");
                }
                return inner;
            }

            if (c is '"' or '[')
            {
                char close = c == '"' ? '"' : ']';
                int start = ++_position;
                while (_position < _text.Length && _text[_position] != close)
                {
                    _position++;
                }
                if (AtEnd)
                {
                    throw new FormatException($"Missing closing {close}.");
                }
                return Value.Text(_text[start.._position++].ToString());
            }

            if (char.IsAsciiDigit(c) || c == '.')
            {
                int start = _position;
                bool integer = true;
                while (_position < _text.Length && (char.IsAsciiDigit(_text[_position]) || _text[_position] == '.'))
                {
                    integer &= _text[_position] != '.';
                    _position++;
                }
                if (!double.TryParse(_text[start.._position], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number))
                {
                    throw new FormatException($"'{_text[start.._position]}' is not a number.");
                }
                if (integer && Math.Abs(number) > long.MaxValue / 2)
                {
                    throw new OverflowException("Number is too large.");
                }
                return Value.Number(number, integer);
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = _position;
                while (_position < _text.Length && (char.IsLetterOrDigit(_text[_position]) || _text[_position] == '_'))
                {
                    _position++;
                }
                ReadOnlySpan<char> word = _text[start.._position];
                if (word is "T")
                {
                    return Value.Boolean(true);
                }
                if (word is "F")
                {
                    return Value.Boolean(false);
                }
                // A bare word is a string, so an unset or unquoted value compares as text.
                return Value.Text(word.ToString());
            }

            throw new FormatException($"Unexpected '{c}' at position {_position + 1}.");
        }

        private static int Compare(Value left, Value right)
        {
            if (left.IsText || right.IsText)
            {
                return Math.Sign(string.CompareOrdinal(left.ToString(), right.ToString()));
            }
            return left.AsNumber().CompareTo(right.AsNumber());
        }
    }
}
