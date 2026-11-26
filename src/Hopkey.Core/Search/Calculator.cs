using System.Globalization;

namespace Hopkey.Core.Search;

/// <summary>Recursive-descent evaluator: + - * / % ^ ( ), unary minus, decimals. Pure.</summary>
public static class Calculator
{
    public static bool LooksLikeExpression(string s)
    {
        bool hasDigit = false, hasOp = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsAsciiDigit(c)) hasDigit = true;
            else if (c is '+' or '*' or '/' or '%' or '^' or '×' or '÷') hasOp = true;
            else if (c == '-' && i > 0) hasOp = true;
            else if (c is '(' or ')' or '.' or ' ' or '-' or ',') { }
            else return false;
        }
        return hasDigit && hasOp;
    }

    public static bool TryEvaluate(string expr, out double result)
    {
        result = 0;
        try
        {
            var p = new Parser(expr.Replace('×', '*').Replace('÷', '/').Replace(",", ""));
            result = p.ParseExpression();
            p.SkipWs();
            if (!p.AtEnd) return false;
            return !double.IsNaN(result) && !double.IsInfinity(result);
        }
        catch { return false; }
    }

    public static string Format(double v)
    {
        if (Math.Abs(v) < 1e15 && Math.Abs(v - Math.Round(v)) < 1e-12) return Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        return v.ToString("G12", CultureInfo.InvariantCulture);
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;
        public Parser(string s) => _s = s;
        public bool AtEnd => _i >= _s.Length;

        public void SkipWs() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        private bool Eat(char c)
        {
            SkipWs();
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }

        public double ParseExpression()
        {
            double v = ParseTerm();
            while (true)
            {
                if (Eat('+')) v += ParseTerm();
                else if (Eat('-')) v -= ParseTerm();
                else return v;
            }
        }

        private double ParseTerm()
        {
            double v = ParseFactor();
            while (true)
            {
                if (Eat('*')) v *= ParseFactor();
                else if (Eat('/')) { var d = ParseFactor(); if (d == 0) throw new DivideByZeroException(); v /= d; }
                else if (Eat('%')) { var d = ParseFactor(); if (d == 0) throw new DivideByZeroException(); v %= d; }
                else return v;
            }
        }

        private double ParseFactor()
        {
            double b = ParseUnary();
            if (Eat('^')) return Math.Pow(b, ParseFactor()); // right associative
            return b;
        }

        private double ParseUnary()
        {
            if (Eat('-')) return -ParseUnary();
            if (Eat('+')) return ParseUnary();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            if (Eat('('))
            {
                double v = ParseExpression();
                if (!Eat(')')) throw new FormatException();
                return v;
            }
            SkipWs();
            int start = _i;
            while (_i < _s.Length && (char.IsAsciiDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (start == _i) throw new FormatException();
            return double.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
