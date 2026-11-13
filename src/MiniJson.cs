using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Minimal dependency-free JSON parser/serializer (objects, arrays,
// strings, numbers, booleans, null). Keeps the app a single EXE with
// no external DLLs. Objects parse to Dictionary<string, object>,
// arrays to List<object>, numbers to double.
public static class MiniJson
{
    public static object Parse(string text)
    {
        var p = new Parser(text);
        p.SkipWs();
        object v = p.ReadValue();
        p.SkipWs();
        return v;
    }

    public static string Write(object value)
    {
        var sb = new StringBuilder();
        WriteValue(sb, value);
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object v)
    {
        if (v == null) { sb.Append("null"); return; }
        if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
        var s = v as string;
        if (s != null) { WriteString(sb, s); return; }
        if (v is double || v is float || v is int || v is long)
        {
            sb.Append(Convert.ToDouble(v).ToString("R", CultureInfo.InvariantCulture));
            return;
        }
        var dict = v as IDictionary;
        if (dict != null)
        {
            sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry e in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, Convert.ToString(e.Key));
                sb.Append(':');
                WriteValue(sb, e.Value);
            }
            sb.Append('}');
            return;
        }
        var list = v as IEnumerable;
        if (list != null)
        {
            sb.Append('[');
            bool first = true;
            foreach (object item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteValue(sb, item);
            }
            sb.Append(']');
            return;
        }
        WriteString(sb, Convert.ToString(v));
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    private sealed class Parser
    {
        private readonly string _t;
        private int _i;

        public Parser(string t) { _t = t ?? ""; }

        public void SkipWs()
        {
            while (_i < _t.Length && (_t[_i] == ' ' || _t[_i] == '\t' || _t[_i] == '\n' || _t[_i] == '\r'))
                _i++;
        }

        public object ReadValue()
        {
            if (_i >= _t.Length) return null;
            char c = _t[_i];
            if (c == '{') return ReadObject();
            if (c == '[') return ReadArray();
            if (c == '"') return ReadString();
            if (c == 't') { Expect("true"); return true; }
            if (c == 'f') { Expect("false"); return false; }
            if (c == 'n') { Expect("null"); return null; }
            return ReadNumber();
        }

        private void Expect(string lit)
        {
            if (_i + lit.Length <= _t.Length && _t.Substring(_i, lit.Length) == lit)
                _i += lit.Length;
            else
                throw new FormatException("Invalid JSON at " + _i);
        }

        private Dictionary<string, object> ReadObject()
        {
            var d = new Dictionary<string, object>();
            _i++; // {
            SkipWs();
            if (_i < _t.Length && _t[_i] == '}') { _i++; return d; }
            while (true)
            {
                SkipWs();
                string key = ReadString();
                SkipWs();
                if (_i < _t.Length && _t[_i] == ':') _i++;
                SkipWs();
                d[key] = ReadValue();
                SkipWs();
                if (_i < _t.Length && _t[_i] == ',') { _i++; continue; }
                if (_i < _t.Length && _t[_i] == '}') { _i++; break; }
                break;
            }
            return d;
        }

        private List<object> ReadArray()
        {
            var l = new List<object>();
            _i++; // [
            SkipWs();
            if (_i < _t.Length && _t[_i] == ']') { _i++; return l; }
            while (true)
            {
                SkipWs();
                l.Add(ReadValue());
                SkipWs();
                if (_i < _t.Length && _t[_i] == ',') { _i++; continue; }
                if (_i < _t.Length && _t[_i] == ']') { _i++; break; }
                break;
            }
            return l;
        }

        private string ReadString()
        {
            var sb = new StringBuilder();
            _i++; // opening quote
            while (_i < _t.Length)
            {
                char c = _t[_i++];
                if (c == '"') break;
                if (c == '\\' && _i < _t.Length)
                {
                    char e = _t[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (_i + 4 <= _t.Length)
                            {
                                sb.Append((char)int.Parse(_t.Substring(_i, 4),
                                    NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                                _i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private double ReadNumber()
        {
            int start = _i;
            while (_i < _t.Length && "-+0123456789.eE".IndexOf(_t[_i]) >= 0) _i++;
            double v;
            double.TryParse(_t.Substring(start, _i - start),
                NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return v;
        }
    }
}
