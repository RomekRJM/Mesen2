using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Mesen.Debugger.Integration
{
	/// <summary>
	/// Location of a C variable, as resolved from the .dbg file
	/// </summary>
	public class CVariableLocation
	{
		public string Name { get; }
		public string? Label { get; }
		public int RelAddress { get; }
		public int SymbolSize { get; }

		//Set for "auto" variables stored on the cc65 C stack
		public string? StackPointer { get; }
		public int StackOffset { get; }

		public bool IsStack => StackPointer != null;
		public string BaseExpression => Label ?? ("$" + RelAddress.ToString("X4"));

		public CVariableLocation(string name, string? label, int relAddress, int symbolSize, string? stackPointer, int stackOffset)
		{
			Name = name;
			Label = label;
			RelAddress = relAddress;
			SymbolSize = Math.Max(1, symbolSize);
			StackPointer = stackPointer;
			StackOffset = stackOffset;
		}
	}

	public record CFunctionScopeInfo(int ScopeID, string Name, string SourcePath);

	public class CWatchEntry
	{
		public string Expression { get; }
		public string Description { get; set; }
		public int RelAddress { get; }

		public CWatchEntry(string expression, string description, int relAddress)
		{
			Expression = expression;
			Description = description;
			RelAddress = relAddress;
		}
	}

	/// <summary>
	/// Result of the analysis of a single C function: the watch entries to display and the info needed to describe arbitrary addresses
	/// </summary>
	public class CFunctionWatchInfo
	{
		public int ScopeID { get; }
		public string FunctionName { get; }
		public List<CWatchEntry> Entries { get; } = new();

		internal List<CSourceAnalyzer.KnownVariable> Variables { get; } = new();
		internal List<(int RelAddress, string Alias)> Aliases { get; } = new();

		public CFunctionWatchInfo(int scopeId, string functionName)
		{
			ScopeID = scopeId;
			FunctionName = functionName;
		}

		/// <summary>
		/// Returns a readable description of the memory at the given CPU address (e.g "currentComboIndex (tmp1)" or "p1Combo.framesLeftToCancel")
		/// </summary>
		public string? Describe(int relAddress, int length)
		{
			CSourceAnalyzer.KnownVariable? match = null;
			foreach(CSourceAnalyzer.KnownVariable v in Variables) {
				if(relAddress >= v.RelAddress && relAddress < v.RelAddress + v.Size) {
					if(match == null || v.Size < match.Size) {
						match = v;
					}
				}
			}

			string? path = null;
			if(match != null) {
				int offset = relAddress - match.RelAddress;
				if(offset == 0 && length >= match.Size && length > 1) {
					path = match.Name;
				} else {
					path = match.Name + CSourceAnalyzer.GetFieldPath(match.Type, offset);
				}
			}

			List<string> aliases = Aliases.Where(a => a.RelAddress == relAddress).Select(a => a.Alias).Distinct().ToList();
			if(path != null) {
				aliases.Remove(path);
			}

			if(aliases.Count > 0) {
				string aliasText = string.Join(" / ", aliases);
				return path != null ? $"{aliasText} ({path})" : aliasText;
			}
			return path;
		}
	}

	/// <summary>
	/// Lightweight C source parser (cc65 dialect) used to find the variables a C function uses.
	/// It handles comments, #include "...", object-like #define macros (used as aliases), #if/#ifdef conditions,
	/// struct/union typedefs and global/local declarations. It is not a full C compiler front-end.
	/// </summary>
	internal class CSourceAnalyzer
	{
		private Dictionary<string, TranslationUnit> _units = new(StringComparer.OrdinalIgnoreCase);
		private Dictionary<int, CFunctionWatchInfo> _functions = new();

		public CFunctionWatchInfo GetFunctionInfo(CFunctionScopeInfo scope, Func<string, CVariableLocation?> resolver)
		{
			if(_functions.TryGetValue(scope.ScopeID, out CFunctionWatchInfo? info)) {
				return info;
			}

			if(!_units.TryGetValue(scope.SourcePath, out TranslationUnit? unit)) {
				unit = new TranslationUnit();
				unit.Load(scope.SourcePath);
				_units[scope.SourcePath] = unit;
			}

			info = new FunctionAnalysis(unit, scope, resolver).Run();
			_functions[scope.ScopeID] = info;
			return info;
		}

		internal static string GetFieldPath(CDeclType? type, int offset)
		{
			if(type == null) {
				return offset > 0 ? "+" + offset : "";
			}

			if(type.IsArray) {
				CDeclType elemType = type.ElementType;
				int elemSize = elemType.Size;
				if(elemSize <= 0) {
					return "[" + offset + "]";
				}
				return "[" + (offset / elemSize) + "]" + GetFieldPath(elemType, offset % elemSize);
			}

			if(type.Struct != null) {
				foreach(CFieldInfo field in type.Struct.Fields) {
					int fieldSize = Math.Max(1, field.Type.Size);
					if(offset >= field.Offset && offset < field.Offset + fieldSize) {
						return "." + field.Name + GetFieldPath(field.Type, offset - field.Offset);
					}
				}
			}

			return offset > 0 ? "+" + offset : "";
		}

		#region Types

		internal class KnownVariable
		{
			public string Name { get; }
			public int RelAddress { get; }
			public int Size { get; }
			public CDeclType? Type { get; }

			public KnownVariable(string name, int relAddress, int size, CDeclType? type)
			{
				Name = name;
				RelAddress = relAddress;
				Size = Math.Max(1, size);
				Type = type;
			}
		}

		internal class CTypeInfo
		{
			private int _size;

			public string Name { get; }
			public bool IsSigned { get; }
			public bool IsPointer { get; }
			public CStructInfo? Struct { get; }

			//Struct sizes are read from the struct itself, to support types that are defined after being forward-declared
			public int Size => Struct?.Size ?? _size;

			public CTypeInfo(string name, int size, bool isSigned = false, bool isPointer = false, CStructInfo? structInfo = null)
			{
				Name = name;
				_size = size;
				IsSigned = isSigned;
				IsPointer = isPointer;
				Struct = structInfo;
			}
		}

		internal class CStructInfo
		{
			public string Name { get; set; } = "";
			public bool IsUnion { get; set; }
			public List<CFieldInfo> Fields { get; } = new();
			public int Size { get; set; }

			public CFieldInfo? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);
		}

		internal record CFieldInfo(string Name, int Offset, CDeclType Type);

		internal class CDeclType
		{
			public CTypeInfo Base { get; }
			public int PointerDepth { get; }
			public List<int> Dims { get; }

			public CDeclType(CTypeInfo baseType, int pointerDepth, List<int> dims)
			{
				Base = baseType;
				PointerDepth = pointerDepth;
				Dims = dims;
			}

			public bool IsArray => Dims.Count > 0;
			public bool IsPointerLike => !IsArray && (PointerDepth > 0 || Base.IsPointer);
			public bool IsSigned => PointerDepth == 0 && Base.IsSigned;
			public int ElementSize => PointerDepth > 0 ? 2 : Base.Size;
			public CStructInfo? Struct => PointerDepth == 0 && Dims.Count == 0 ? Base.Struct : null;
			public CDeclType ElementType => new CDeclType(Base, PointerDepth, Dims.Skip(1).ToList());

			public int Size
			{
				get
				{
					int size = ElementSize;
					foreach(int dim in Dims) {
						if(dim < 0) {
							return -1;
						}
						size *= dim;
					}
					return size;
				}
			}
		}

		private enum TokType { Identifier, Number, String, Punct }

		private class Tok
		{
			public string Text { get; }
			public TokType Type { get; }

			//Set when the token was produced by the expansion of a macro (outermost macro name)
			public string? Alias { get; set; }
			public int ExpansionId { get; set; }
			public bool ExpansionFirst { get; set; }
			public int ExpansionLength { get; set; }

			public Tok(string text, TokType type)
			{
				Text = text;
				Type = type;
			}

			public Tok Clone() => new Tok(Text, Type);
			public override string ToString() => Text;
		}

		private class Macro
		{
			public string Name { get; }
			public bool IsFunctionLike { get; }
			public List<Tok> Body { get; }
			public List<Tok> ExpandedBody { get; set; } = new();
			public bool IsMain { get; set; }
			public int DefTokenIndex { get; set; }
			public int UndefTokenIndex { get; set; } = int.MaxValue;

			public Macro(string name, bool isFunctionLike, List<Tok> body)
			{
				Name = name;
				IsFunctionLike = isFunctionLike;
				Body = body;
			}
		}

		private class FunctionDef
		{
			public string Name { get; }
			public int ParamStart { get; }
			public int ParamEnd { get; }
			public int BodyStart { get; }
			public int BodyEnd { get; }

			public FunctionDef(string name, int paramStart, int paramEnd, int bodyStart, int bodyEnd)
			{
				Name = name;
				ParamStart = paramStart;
				ParamEnd = paramEnd;
				BodyStart = bodyStart;
				BodyEnd = bodyEnd;
			}
		}

		private struct CondState
		{
			public bool ParentActive;
			public bool Taken;
			public bool Active;
		}

		private static readonly HashSet<string> _qualifiers = new() {
			"const", "volatile", "static", "extern", "register", "auto", "inline", "typedef", "restrict",
			"__fastcall__", "__cdecl__", "fastcall", "cdecl", "__near__", "__far__", "near", "far", "__inline__", "__restrict__"
		};

		private static readonly HashSet<string> _primitiveTypes = new() {
			"void", "char", "short", "int", "long", "signed", "unsigned", "float", "double", "_Bool", "bool"
		};

		private static readonly Dictionary<string, CTypeInfo> _wellKnownTypedefs = new() {
			{ "uint8_t", new CTypeInfo("uint8_t", 1) }, { "int8_t", new CTypeInfo("int8_t", 1, true) },
			{ "uint16_t", new CTypeInfo("uint16_t", 2) }, { "int16_t", new CTypeInfo("int16_t", 2, true) },
			{ "uint32_t", new CTypeInfo("uint32_t", 4) }, { "int32_t", new CTypeInfo("int32_t", 4, true) },
			{ "size_t", new CTypeInfo("size_t", 2) }, { "byte", new CTypeInfo("byte", 1) }, { "word", new CTypeInfo("word", 2) },
		};

		private static readonly HashSet<string> _keywords = new() {
			"if", "else", "while", "for", "do", "switch", "case", "default", "break", "continue", "return", "goto", "sizeof",
			"typedef", "struct", "union", "enum", "asm", "__asm__", "__A__", "__X__", "__AX__", "__EAX__", "NULL", "true", "false"
		};

		#endregion

		#region Translation unit (preprocessor + declarations)

		private class TranslationUnit
		{
			public List<Tok> Tokens { get; } = new();
			public Dictionary<string, Macro> Macros { get; } = new();
			public List<Macro> MainMacros { get; } = new();
			public Dictionary<string, CDeclType> Globals { get; } = new();
			public Dictionary<string, CDeclType> Typedefs { get; } = new();
			public Dictionary<string, CStructInfo> StructTags { get; } = new();
			public Dictionary<string, FunctionDef> Functions { get; } = new();

			private HashSet<string> _onceFiles = new(StringComparer.OrdinalIgnoreCase);
			private int _expansionCounter = 0;

			public void Load(string mainFile)
			{
				Preprocess(mainFile, true, 0);
				new DeclarationParser(this, Tokens).ParseTopLevel();
			}

			private void Preprocess(string path, bool isMain, int depth)
			{
				string fullPath;
				try {
					fullPath = Path.GetFullPath(path);
				} catch {
					return;
				}

				if(depth > 32 || _onceFiles.Contains(fullPath) || !File.Exists(fullPath)) {
					return;
				}

				string[] lines = StripComments(File.ReadAllText(fullPath)).Split('\n');
				Stack<CondState> conditions = new();

				for(int i = 0; i < lines.Length; i++) {
					string line = lines[i].TrimEnd('\r');
					while(line.EndsWith("\\") && i + 1 < lines.Length) {
						line = line.Substring(0, line.Length - 1) + " " + lines[++i].TrimEnd('\r');
					}

					bool isActive = conditions.Count == 0 || conditions.Peek().Active;
					string trimmed = line.TrimStart();
					if(trimmed.StartsWith("#")) {
						string directiveLine = trimmed.Substring(1).TrimStart();
						int p = 0;
						while(p < directiveLine.Length && char.IsLetter(directiveLine[p])) {
							p++;
						}
						string directive = directiveLine.Substring(0, p);
						string rest = directiveLine.Substring(p).Trim();

						switch(directive) {
							case "if": {
								bool value = isActive && EvaluateCondition(rest);
								conditions.Push(new CondState() { ParentActive = isActive, Taken = value, Active = value });
								break;
							}

							case "ifdef":
							case "ifndef": {
								bool defined = Macros.ContainsKey(ReadIdentifier(rest, 0));
								bool value = isActive && (directive == "ifdef" ? defined : !defined);
								conditions.Push(new CondState() { ParentActive = isActive, Taken = value, Active = value });
								break;
							}

							case "elif":
								if(conditions.Count > 0) {
									CondState state = conditions.Pop();
									if(state.Taken) {
										state.Active = false;
									} else {
										state.Active = state.ParentActive && EvaluateCondition(rest);
										state.Taken = state.Active;
									}
									conditions.Push(state);
								}
								break;

							case "else":
								if(conditions.Count > 0) {
									CondState state = conditions.Pop();
									state.Active = state.ParentActive && !state.Taken;
									state.Taken = true;
									conditions.Push(state);
								}
								break;

							case "endif":
								if(conditions.Count > 0) {
									conditions.Pop();
								}
								break;

							default:
								if(!isActive) {
									break;
								}

								if(directive == "define") {
									DefineMacro(rest, isMain);
								} else if(directive == "undef") {
									if(Macros.Remove(ReadIdentifier(rest, 0), out Macro? macro) && macro.IsMain) {
										macro.UndefTokenIndex = Tokens.Count;
									}
								} else if(directive == "include") {
									if(rest.StartsWith("\"")) {
										int end = rest.IndexOf('"', 1);
										if(end > 1) {
											string includePath = rest.Substring(1, end - 1);
											Preprocess(Path.Combine(Path.GetDirectoryName(fullPath) ?? "", includePath), false, depth + 1);
										}
									}
								} else if(directive == "pragma") {
									if(rest == "once") {
										_onceFiles.Add(fullPath);
									}
								}
								break;
						}
						continue;
					}

					if(isActive) {
						Tokens.AddRange(Expand(Tokenize(line), new HashSet<string>()));
					}
				}
			}

			private void DefineMacro(string text, bool isMain)
			{
				string name = ReadIdentifier(text, 0);
				if(name.Length == 0) {
					return;
				}

				int p = name.Length;
				bool isFunctionLike = p < text.Length && text[p] == '(';
				if(isFunctionLike) {
					int end = text.IndexOf(')', p);
					p = end < 0 ? text.Length : end + 1;
				}

				List<Tok> body = Tokenize(text.Substring(p));
				StripEnclosingParentheses(body);

				if(Macros.TryGetValue(name, out Macro? previous) && previous.IsMain) {
					//Redefinition, the previous alias is no longer visible after this point
					previous.UndefTokenIndex = Tokens.Count;
				}

				Macro macro = new Macro(name, isFunctionLike, body) { IsMain = isMain, DefTokenIndex = Tokens.Count };
				if(!isFunctionLike) {
					macro.ExpandedBody = Expand(body.Select(t => t.Clone()).ToList(), new HashSet<string>() { name });
				}

				Macros[name] = macro;
				if(isMain && !isFunctionLike) {
					MainMacros.Add(macro);
				}
			}

			private List<Tok> Expand(List<Tok> input, HashSet<string> disabled)
			{
				List<Tok> output = new(input.Count);
				foreach(Tok tok in input) {
					if(tok.Type == TokType.Identifier && !disabled.Contains(tok.Text) && Macros.TryGetValue(tok.Text, out Macro? macro) && !macro.IsFunctionLike) {
						bool isOuter = tok.Alias == null;
						string alias = tok.Alias ?? tok.Text;
						int expansionId = isOuter ? ++_expansionCounter : tok.ExpansionId;

						List<Tok> body = macro.Body.Select(b => new Tok(b.Text, b.Type) { Alias = alias, ExpansionId = expansionId }).ToList();
						disabled.Add(macro.Name);
						List<Tok> expanded = Expand(body, disabled);
						disabled.Remove(macro.Name);

						if(isOuter && expanded.Count > 0) {
							expanded[0].ExpansionFirst = true;
							expanded[0].ExpansionLength = expanded.Count;
						}
						output.AddRange(expanded);
					} else {
						output.Add(tok);
					}
				}
				return output;
			}

			private bool EvaluateCondition(string text)
			{
				List<Tok> tokens = Tokenize(text);
				List<Tok> resolved = new();
				for(int i = 0; i < tokens.Count; i++) {
					if(tokens[i].Text == "defined") {
						string name = "";
						if(i + 1 < tokens.Count && tokens[i + 1].Text == "(") {
							if(i + 2 < tokens.Count) {
								name = tokens[i + 2].Text;
							}
							i += 3;
						} else if(i + 1 < tokens.Count) {
							name = tokens[i + 1].Text;
							i++;
						}
						resolved.Add(new Tok(Macros.ContainsKey(name) ? "1" : "0", TokType.Number));
					} else {
						resolved.Add(tokens[i]);
					}
				}

				try {
					return new ConstExpression(Expand(resolved, new HashSet<string>())).Evaluate() != 0;
				} catch {
					return false;
				}
			}

			public long EvaluateConstant(List<Tok> tokens)
			{
				try {
					return new ConstExpression(tokens).Evaluate();
				} catch {
					return -1;
				}
			}
		}

		private class DeclarationParser
		{
			private TranslationUnit _unit;
			private List<Tok> _tokens;
			private int _end;

			public DeclarationParser(TranslationUnit unit, List<Tok> tokens, int end = -1)
			{
				_unit = unit;
				_tokens = tokens;
				_end = end < 0 ? tokens.Count : end;
			}

			private string Peek(int pos) => pos < _end ? _tokens[pos].Text : "";

			public void ParseTopLevel()
			{
				int pos = 0;
				while(pos < _end) {
					int start = pos;
					try {
						ParseDeclaration(ref pos, true, _unit.Globals);
					} catch {
						pos = start;
						SkipStatement(ref pos);
					}

					if(pos <= start) {
						pos = start + 1;
					}
				}
			}

			/// <summary>
			/// Parses declarations found anywhere in the given range (used for function parameters/bodies)
			/// </summary>
			public void ParseLocalDeclarations(int start, Dictionary<string, CDeclType> output)
			{
				for(int pos = start; pos < _end; pos++) {
					string prev = pos > 0 ? _tokens[pos - 1].Text : ";";
					if((prev == "{" || prev == "}" || prev == ";" || prev == "(" || prev == ",") && IsTypeStart(pos)) {
						int p = pos;
						try {
							ParseDeclaration(ref p, false, output);
						} catch { }
					}
				}
			}

			private bool IsTypeStart(int pos)
			{
				Tok tok = _tokens[pos];
				if(tok.Type != TokType.Identifier) {
					return false;
				}

				if(_qualifiers.Contains(tok.Text) || _primitiveTypes.Contains(tok.Text) || tok.Text == "struct" || tok.Text == "union" || tok.Text == "enum") {
					return true;
				}

				if(_unit.Typedefs.ContainsKey(tok.Text) || _wellKnownTypedefs.ContainsKey(tok.Text)) {
					string next = Peek(pos + 1);
					return next == "*" || (pos + 1 < _end && _tokens[pos + 1].Type == TokType.Identifier);
				}
				return false;
			}

			private void ParseDeclaration(ref int pos, bool isTopLevel, Dictionary<string, CDeclType> output)
			{
				if(Peek(pos) == ";") {
					pos++;
					return;
				}

				CTypeInfo? baseType = ParseSpecifiers(ref pos, out bool isTypedef);
				if(baseType == null) {
					if(isTopLevel) {
						SkipStatement(ref pos);
					}
					return;
				}

				while(pos < _end) {
					if(Peek(pos) == ";") {
						pos++;
						return;
					}

					int declStart = pos;
					ParseDeclarator(ref pos, baseType, out string? name, out CDeclType type, out int paramStart, out int paramEnd);

					if(paramStart >= 0 && Peek(pos) == "{") {
						//Function definition
						int bodyEnd = FindMatching(pos);
						if(isTopLevel && name != null) {
							_unit.Functions[name] = new FunctionDef(name, paramStart, paramEnd, pos, bodyEnd);
						}
						pos = bodyEnd + 1;
						return;
					}

					if(Peek(pos) == "=") {
						pos++;
						if(Peek(pos) == "{") {
							int end = FindMatching(pos);
							if(type.Dims.Count > 0 && type.Dims[0] < 0) {
								type.Dims[0] = CountInitializerElements(pos, end);
							}
							pos = end + 1;
						} else if(pos < _end && _tokens[pos].Type == TokType.String && type.Dims.Count > 0 && type.Dims[0] < 0) {
							type.Dims[0] = Math.Max(0, _tokens[pos].Text.Length - 2) + 1;
						}

						int depth = 0;
						while(pos < _end) {
							string t = Peek(pos);
							if(t == "(" || t == "{" || t == "[") {
								depth++;
							} else if(t == ")" || t == "}" || t == "]") {
								depth--;
							} else if(depth <= 0 && (t == "," || t == ";")) {
								break;
							}
							pos++;
						}
					}

					if(name != null && paramStart < 0) {
						if(isTypedef) {
							_unit.Typedefs[name] = type;
						} else if(!output.TryGetValue(name, out CDeclType? existing) || existing.Size < 0 || type.Size > 0) {
							output[name] = type;
						}
					}

					if(Peek(pos) == ",") {
						pos++;
					} else if(Peek(pos) == ";") {
						pos++;
						return;
					} else {
						if(isTopLevel) {
							SkipStatement(ref pos);
						}
						return;
					}

					if(pos <= declStart) {
						return;
					}
				}
			}

			private CTypeInfo? ParseSpecifiers(ref int pos, out bool isTypedef)
			{
				isTypedef = false;
				bool isUnsigned = false, isSigned = false, sawPrimitive = false;
				int longCount = 0;
				string? primitive = null;
				CTypeInfo? result = null;

				while(pos < _end) {
					Tok tok = _tokens[pos];
					if(tok.Type != TokType.Identifier) {
						break;
					}

					string t = tok.Text;
					if(_qualifiers.Contains(t)) {
						if(t == "typedef") {
							isTypedef = true;
						}
						pos++;
					} else if(_primitiveTypes.Contains(t)) {
						sawPrimitive = true;
						switch(t) {
							case "unsigned": isUnsigned = true; break;
							case "signed": isSigned = true; break;
							case "long": longCount++; break;
							default: primitive ??= t; break;
						}
						pos++;
					} else if(t == "struct" || t == "union") {
						pos++;
						result = ParseStructSpecifier(ref pos, t == "union");
					} else if(t == "enum") {
						pos++;
						if(pos < _end && _tokens[pos].Type == TokType.Identifier) {
							pos++;
						}
						if(Peek(pos) == "{") {
							pos = FindMatching(pos) + 1;
						}
						result = new CTypeInfo("enum", 2, true);
					} else if(result == null && !sawPrimitive && _unit.Typedefs.TryGetValue(t, out CDeclType? typedefType)) {
						pos++;
						result = FromTypedef(t, typedefType);
					} else if(result == null && !sawPrimitive && _wellKnownTypedefs.TryGetValue(t, out CTypeInfo? knownType)) {
						pos++;
						result = knownType;
					} else {
						break;
					}
				}

				if(result != null) {
					return result;
				}

				if(!sawPrimitive) {
					return null;
				}

				if(primitive == "char") {
					return new CTypeInfo((isSigned ? "signed " : isUnsigned ? "unsigned " : "") + "char", 1, isSigned);
				} else if(primitive == "void") {
					return new CTypeInfo("void", 1);
				} else if(primitive == "_Bool" || primitive == "bool") {
					return new CTypeInfo("bool", 1);
				} else if(primitive == "float" || primitive == "double") {
					return new CTypeInfo(primitive, 4, true);
				} else if(primitive == "short") {
					return new CTypeInfo("short", 2, !isUnsigned);
				} else if(longCount > 0) {
					return new CTypeInfo("long", 4, !isUnsigned);
				} else {
					return new CTypeInfo("int", 2, !isUnsigned);
				}
			}

			private static CTypeInfo FromTypedef(string name, CDeclType type)
			{
				if(type.IsArray) {
					return new CTypeInfo(name, Math.Max(0, type.Size));
				} else if(type.PointerDepth > 0) {
					return new CTypeInfo(name, 2, false, true);
				}
				return type.Base;
			}

			private CTypeInfo ParseStructSpecifier(ref int pos, bool isUnion)
			{
				string? tag = null;
				if(pos < _end && _tokens[pos].Type == TokType.Identifier) {
					tag = _tokens[pos].Text;
					pos++;
				}

				CStructInfo? structInfo = null;
				if(Peek(pos) == "{") {
					structInfo = ParseStructBody(ref pos, isUnion);
					structInfo.Name = tag ?? "";
					if(tag != null) {
						_unit.StructTags[tag] = structInfo;
					}
				} else if(tag != null && !_unit.StructTags.TryGetValue(tag, out structInfo)) {
					//Forward declaration, fill it in later if it gets defined
					structInfo = new CStructInfo() { Name = tag, IsUnion = isUnion };
					_unit.StructTags[tag] = structInfo;
				}

				structInfo ??= new CStructInfo() { IsUnion = isUnion };
				return new CTypeInfo(structInfo.Name, 0, false, false, structInfo);
			}

			private CStructInfo ParseStructBody(ref int pos, bool isUnion)
			{
				CStructInfo info = new CStructInfo() { IsUnion = isUnion };
				int end = FindMatching(pos);
				pos++;

				int offset = 0;
				int maxSize = 0;
				void AddField(string? name, CDeclType type)
				{
					int size = Math.Max(0, type.Size);
					int fieldOffset = isUnion ? 0 : offset;
					if(name != null) {
						info.Fields.Add(new CFieldInfo(name, fieldOffset, type));
					} else if(type.Struct != null) {
						//Anonymous struct/union member, flatten its fields
						foreach(CFieldInfo field in type.Struct.Fields) {
							info.Fields.Add(new CFieldInfo(field.Name, fieldOffset + field.Offset, field.Type));
						}
					}
					offset = isUnion ? offset : offset + size;
					maxSize = Math.Max(maxSize, size);
				}

				while(pos < end) {
					CTypeInfo? baseType = ParseSpecifiers(ref pos, out _);
					if(baseType == null) {
						SkipTo(ref pos, end, ";");
						pos++;
						continue;
					}

					if(Peek(pos) == ";") {
						if(baseType.Struct != null && baseType.Struct.Name.Length == 0) {
							AddField(null, new CDeclType(baseType, 0, new()));
						}
						pos++;
						continue;
					}

					while(pos < end) {
						int declStart = pos;
						ParseDeclarator(ref pos, baseType, out string? name, out CDeclType type, out _, out _);
						if(Peek(pos) == ":") {
							//Bitfield, size is approximated by the base type
							SkipTo(ref pos, end, ",", ";");
						}

						AddField(name, type);

						if(Peek(pos) == "," && pos > declStart) {
							pos++;
							continue;
						}
						SkipTo(ref pos, end, ";");
						break;
					}
					pos++;
				}

				info.Size = isUnion ? maxSize : offset;
				pos = end + 1;
				return info;
			}

			private void ParseDeclarator(ref int pos, CTypeInfo baseType, out string? name, out CDeclType type, out int paramStart, out int paramEnd)
			{
				name = null;
				paramStart = -1;
				paramEnd = -1;

				int pointerDepth = 0;
				while(pos < _end) {
					string t = Peek(pos);
					if(t == "*") {
						pointerDepth++;
						pos++;
					} else if(_qualifiers.Contains(t)) {
						pos++;
					} else {
						break;
					}
				}

				bool isFunctionPointer = false;
				if(Peek(pos) == "(" && Peek(pos + 1) == "*") {
					//Function pointer or pointer to array: (*name)(...) / (*name)[...]
					pos += 2;
					while(Peek(pos) == "*" || _qualifiers.Contains(Peek(pos))) {
						pos++;
					}
					if(pos < _end && _tokens[pos].Type == TokType.Identifier) {
						name = _tokens[pos].Text;
						pos++;
					}
					if(Peek(pos) == ")") {
						pos++;
					}
					isFunctionPointer = true;
				} else if(pos < _end && _tokens[pos].Type == TokType.Identifier && !_keywords.Contains(_tokens[pos].Text) && !_primitiveTypes.Contains(_tokens[pos].Text) && !_qualifiers.Contains(_tokens[pos].Text)) {
					name = _tokens[pos].Text;
					pos++;
				}

				if(Peek(pos) == "(") {
					int end = FindMatching(pos);
					if(!isFunctionPointer) {
						paramStart = pos;
						paramEnd = end;
					}
					pos = end + 1;
				}

				List<int> dims = new();
				while(Peek(pos) == "[") {
					int end = FindMatching(pos);
					List<Tok> dimTokens = _tokens.GetRange(pos + 1, Math.Max(0, end - pos - 1));
					dims.Add(dimTokens.Count == 0 ? -1 : (int)_unit.EvaluateConstant(dimTokens));
					pos = end + 1;
				}

				if(isFunctionPointer) {
					type = new CDeclType(new CTypeInfo("function pointer", 2, false, true), 0, dims);
				} else {
					type = new CDeclType(baseType, pointerDepth, dims);
				}
			}

			private int CountInitializerElements(int start, int end)
			{
				int count = 0;
				int depth = 0;
				bool hasContent = false;
				for(int i = start + 1; i < end; i++) {
					string t = _tokens[i].Text;
					if(t == "{" || t == "(" || t == "[") {
						depth++;
					} else if(t == "}" || t == ")" || t == "]") {
						depth--;
					} else if(t == "," && depth == 0) {
						count++;
						hasContent = false;
						continue;
					}
					hasContent = true;
				}
				return hasContent ? count + 1 : count;
			}

			private int FindMatching(int pos)
			{
				string open = _tokens[pos].Text;
				string close = open == "(" ? ")" : open == "[" ? "]" : "}";
				int depth = 0;
				for(int i = pos; i < _end; i++) {
					string t = _tokens[i].Text;
					if(t == open) {
						depth++;
					} else if(t == close) {
						depth--;
						if(depth == 0) {
							return i;
						}
					}
				}
				return _end - 1;
			}

			private void SkipTo(ref int pos, int end, params string[] stopTokens)
			{
				int depth = 0;
				while(pos < end) {
					string t = _tokens[pos].Text;
					if(t == "(" || t == "{" || t == "[") {
						depth++;
					} else if(t == ")" || t == "}" || t == "]") {
						depth--;
					} else if(depth <= 0 && stopTokens.Contains(t)) {
						return;
					}
					pos++;
				}
			}

			private void SkipStatement(ref int pos)
			{
				int depth = 0;
				while(pos < _end) {
					string t = _tokens[pos].Text;
					pos++;
					if(t == "(" || t == "{" || t == "[") {
						depth++;
					} else if(t == ")" || t == "]") {
						depth--;
					} else if(t == "}") {
						depth--;
						if(depth <= 0 && Peek(pos) != ";") {
							return;
						}
					} else if(t == ";" && depth <= 0) {
						return;
					}
				}
			}
		}

		#endregion

		#region Function analysis

		private class FunctionAnalysis
		{
			private TranslationUnit _unit;
			private CFunctionScopeInfo _scope;
			private Func<string, CVariableLocation?> _resolver;
			private Dictionary<string, CVariableLocation?> _resolved = new();
			private Dictionary<string, CDeclType> _locals = new();
			private CFunctionWatchInfo _info;
			private Dictionary<string, CWatchEntry> _entriesByExpression = new();

			public FunctionAnalysis(TranslationUnit unit, CFunctionScopeInfo scope, Func<string, CVariableLocation?> resolver)
			{
				_unit = unit;
				_scope = scope;
				_resolver = resolver;
				_info = new CFunctionWatchInfo(scope.ScopeID, scope.Name);
			}

			private CVariableLocation? Resolve(string name)
			{
				if(!_resolved.TryGetValue(name, out CVariableLocation? loc)) {
					loc = _resolver(name);
					_resolved[name] = loc;
				}
				return loc;
			}

			public CFunctionWatchInfo Run()
			{
				_unit.Functions.TryGetValue(_scope.Name, out FunctionDef? function);
				List<Tok> tokens = _unit.Tokens;

				if(function != null) {
					new DeclarationParser(_unit, tokens, function.ParamEnd).ParseLocalDeclarations(function.ParamStart + 1, _locals);
					new DeclarationParser(_unit, tokens, function.BodyEnd).ParseLocalDeclarations(function.BodyStart + 1, _locals);
				}

				//1. Aliases (#define name variable) defined in the C file and visible in this function
				int bodyIndex = function?.BodyStart ?? tokens.Count;
				foreach(Macro macro in _unit.MainMacros) {
					if(macro.DefTokenIndex <= bodyIndex && macro.UndefTokenIndex > bodyIndex && macro.ExpandedBody.Count > 0 && macro.ExpandedBody[0].Type == TokType.Identifier) {
						AddAccess(macro.ExpandedBody, 0, macro.ExpandedBody.Count, macro.Name);
					}
				}

				//2. Variables used in the function's body
				if(function != null) {
					for(int i = function.BodyStart + 1; i < function.BodyEnd; i++) {
						Tok tok = tokens[i];
						if(tok.Type != TokType.Identifier || _keywords.Contains(tok.Text) || _qualifiers.Contains(tok.Text) || _primitiveTypes.Contains(tok.Text) || _unit.Typedefs.ContainsKey(tok.Text)) {
							continue;
						}

						string prev = tokens[i - 1].Text;
						string next = tokens[i + 1].Text;
						if(prev == "." || prev == "->" || prev == "goto" || next == "(") {
							//Field name, label or function call
							continue;
						}

						int end = AddAccess(tokens, i, function.BodyEnd, null);
						i = Math.Max(i, end - 1);
					}
				}

				_info.Entries.AddRange(_entriesByExpression.Values);

				//Data used to describe arbitrary addresses (for manually added watch entries)
				foreach(KeyValuePair<string, CDeclType> kvp in _unit.Globals.Concat(_locals)) {
					CVariableLocation? loc = Resolve(kvp.Key);
					if(loc != null && !loc.IsStack && loc.RelAddress >= 0) {
						int size = kvp.Value.Size > 0 ? kvp.Value.Size : loc.SymbolSize;
						_info.Variables.Add(new KnownVariable(kvp.Key, loc.RelAddress, size, kvp.Value));
					}
				}

				return _info;
			}

			/// <summary>
			/// Processes a variable access (e.g: "var", "var.field.field", "var[x]") starting at the given index and returns the index after the access
			/// </summary>
			private int AddAccess(List<Tok> tokens, int start, int end, string? aliasOverride)
			{
				Tok tok = tokens[start];
				string name = tok.Text;
				CVariableLocation? loc = Resolve(name);
				if(loc == null) {
					return start + 1;
				}

				_locals.TryGetValue(name, out CDeclType? type);
				if(type == null) {
					_unit.Globals.TryGetValue(name, out type);
				}

				//Follow struct field accesses (var.field.field)
				int offset = 0;
				List<(int TokenIndex, string Name)> fields = new();
				int pos = start + 1;
				while(pos + 1 < end && tokens[pos].Text == "." && type?.Struct != null) {
					CFieldInfo? field = type.Struct.FindField(tokens[pos + 1].Text);
					if(field == null) {
						break;
					}

					offset += field.Offset;
					type = field.Type;
					fields.Add((pos + 1, field.Name));
					pos += 2;
				}

				string description;
				bool isAlias;
				if(aliasOverride != null) {
					if(pos != end) {
						//Macro is not a simple variable access (e.g an expression), ignore it
						return pos;
					}
					description = aliasOverride;
					isAlias = true;
				} else if(tok.Alias != null && tok.ExpansionFirst && start + tok.ExpansionLength <= pos) {
					//The whole macro expansion is part of this access, use the macro's name (+ any field accessed after it)
					int aliasEnd = start + tok.ExpansionLength;
					description = tok.Alias + string.Concat(fields.Where(f => f.TokenIndex >= aliasEnd).Select(f => "." + f.Name));
					isAlias = true;
				} else {
					description = name + string.Concat(fields.Select(f => "." + f.Name));
					isAlias = false;
				}

				AddEntry(loc, offset, type, description, isAlias);
				return pos;
			}

			private void AddEntry(CVariableLocation loc, int offset, CDeclType? type, string description, bool isAlias)
			{
				string? expression;
				int relAddress = -1;

				if(loc.IsStack) {
					int elementSize = type == null ? 1 : (type.IsArray || type.Struct != null ? -1 : type.ElementSize);
					int stackOffset = loc.StackOffset + offset;
					string address = "{" + loc.StackPointer + "}" + (stackOffset != 0 ? "+" + stackOffset : "");
					if(elementSize == 1) {
						expression = $"[{address}], {(type?.IsSigned == true ? "S" : "U")}1";
					} else if(elementSize == 2) {
						expression = $"{{{address}}}, {(type!.IsPointerLike ? "H" : type.IsSigned ? "S" : "U")}2";
					} else {
						//Arrays/structs on the C stack are not supported by the watch syntax
						return;
					}
					description += " (C stack)";
				} else {
					string address = loc.BaseExpression + (offset > 0 ? "+" + offset : "");
					relAddress = loc.RelAddress >= 0 ? loc.RelAddress + offset : -1;

					bool isBlock = type != null ? (type.IsArray || type.Struct != null) : (loc.SymbolSize - offset > 1);
					if(isBlock) {
						int size = type?.Size ?? -1;
						if(size <= 0) {
							size = loc.SymbolSize - offset;
						}
						expression = $"[{address}, {Math.Max(1, size)}]";
					} else {
						int elementSize = type?.ElementSize ?? 1;
						if(elementSize == 1) {
							expression = $"[{address}], {(type?.IsSigned == true ? "S" : "U")}1";
						} else if(elementSize == 2) {
							expression = $"{{{address}}}, {(type!.IsPointerLike ? "H" : type.IsSigned ? "S" : "U")}2";
						} else {
							expression = $"[{address}, {elementSize}]";
						}
					}

					if(isAlias && relAddress >= 0) {
						_info.Aliases.Add((relAddress, description));
					}
				}

				if(_entriesByExpression.TryGetValue(expression, out CWatchEntry? entry)) {
					List<string> descriptions = entry.Description.Split(" / ").ToList();
					if(!descriptions.Contains(description)) {
						entry.Description += " / " + description;
					}
				} else {
					_entriesByExpression[expression] = new CWatchEntry(expression, description, relAddress);
				}
			}
		}

		#endregion

		#region Lexer / constant expressions

		private static string ReadIdentifier(string text, int start)
		{
			int p = start;
			while(p < text.Length && (char.IsLetterOrDigit(text[p]) || text[p] == '_')) {
				p++;
			}
			return text.Substring(start, p - start);
		}

		private static void StripEnclosingParentheses(List<Tok> tokens)
		{
			while(tokens.Count >= 2 && tokens[0].Text == "(" && tokens[^1].Text == ")") {
				int depth = 0;
				for(int i = 0; i < tokens.Count; i++) {
					if(tokens[i].Text == "(") {
						depth++;
					} else if(tokens[i].Text == ")") {
						depth--;
						if(depth == 0 && i != tokens.Count - 1) {
							return;
						}
					}
				}
				tokens.RemoveAt(tokens.Count - 1);
				tokens.RemoveAt(0);
			}
		}

		private static string StripComments(string text)
		{
			StringBuilder sb = new(text.Length);
			int i = 0;
			while(i < text.Length) {
				char c = text[i];
				char next = i + 1 < text.Length ? text[i + 1] : '\0';
				if(c == '/' && next == '/') {
					while(i < text.Length && text[i] != '\n') {
						i++;
					}
				} else if(c == '/' && next == '*') {
					i += 2;
					while(i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/')) {
						if(text[i] == '\n') {
							sb.Append('\n');
						}
						i++;
					}
					i += 2;
					sb.Append(' ');
				} else if(c == '"' || c == '\'') {
					sb.Append(c);
					i++;
					while(i < text.Length && text[i] != c && text[i] != '\n') {
						if(text[i] == '\\' && i + 1 < text.Length) {
							sb.Append(text[i]);
							i++;
						}
						sb.Append(text[i]);
						i++;
					}
					if(i < text.Length && text[i] == c) {
						sb.Append(c);
						i++;
					}
				} else {
					sb.Append(c);
					i++;
				}
			}
			return sb.ToString();
		}

		private static readonly string[] _punctuators = {
			"<<=", ">>=", "...", "->", "++", "--", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||",
			"+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "##"
		};

		private static List<Tok> Tokenize(string line)
		{
			List<Tok> tokens = new();
			int i = 0;
			while(i < line.Length) {
				char c = line[i];
				if(char.IsWhiteSpace(c)) {
					i++;
				} else if(char.IsLetter(c) || c == '_') {
					int start = i;
					while(i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) {
						i++;
					}
					tokens.Add(new Tok(line.Substring(start, i - start), TokType.Identifier));
				} else if(char.IsDigit(c) || (c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1]))) {
					int start = i;
					while(i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '.')) {
						i++;
					}
					tokens.Add(new Tok(line.Substring(start, i - start), TokType.Number));
				} else if(c == '"' || c == '\'') {
					int start = i;
					i++;
					while(i < line.Length && line[i] != c) {
						if(line[i] == '\\') {
							i++;
						}
						i++;
					}
					i = Math.Min(i + 1, line.Length);
					tokens.Add(new Tok(line.Substring(start, i - start), c == '"' ? TokType.String : TokType.Number));
				} else {
					string? punct = _punctuators.FirstOrDefault(p => string.CompareOrdinal(line, i, p, 0, p.Length) == 0);
					punct ??= c.ToString();
					tokens.Add(new Tok(punct, TokType.Punct));
					i += punct.Length;
				}
			}
			return tokens;
		}

		/// <summary>
		/// Evaluates constant integer expressions (used for #if conditions and array sizes). Unknown identifiers evaluate to 0, like the C preprocessor.
		/// </summary>
		private class ConstExpression
		{
			private List<Tok> _tokens;
			private int _pos = 0;

			private static readonly Dictionary<string, int> _precedence = new() {
				{ "||", 1 }, { "&&", 2 }, { "|", 3 }, { "^", 4 }, { "&", 5 },
				{ "==", 6 }, { "!=", 6 }, { "<", 7 }, { ">", 7 }, { "<=", 7 }, { ">=", 7 },
				{ "<<", 8 }, { ">>", 8 }, { "+", 9 }, { "-", 9 }, { "*", 10 }, { "/", 10 }, { "%", 10 }
			};

			public ConstExpression(List<Tok> tokens)
			{
				_tokens = tokens;
			}

			private string Peek => _pos < _tokens.Count ? _tokens[_pos].Text : "";

			public long Evaluate()
			{
				long result = ParseTernary();
				if(_pos != _tokens.Count) {
					throw new Exception("Unexpected token");
				}
				return result;
			}

			private long ParseTernary()
			{
				long cond = ParseBinary(1);
				if(Peek == "?") {
					_pos++;
					long a = ParseTernary();
					if(Peek != ":") {
						throw new Exception("Expected :");
					}
					_pos++;
					long b = ParseTernary();
					return cond != 0 ? a : b;
				}
				return cond;
			}

			private long ParseBinary(int minPrecedence)
			{
				long left = ParseUnary();
				while(_precedence.TryGetValue(Peek, out int precedence) && precedence >= minPrecedence) {
					string op = Peek;
					_pos++;
					long right = ParseBinary(precedence + 1);
					left = op switch {
						"||" => (left != 0 || right != 0) ? 1 : 0,
						"&&" => (left != 0 && right != 0) ? 1 : 0,
						"|" => left | right,
						"^" => left ^ right,
						"&" => left & right,
						"==" => left == right ? 1 : 0,
						"!=" => left != right ? 1 : 0,
						"<" => left < right ? 1 : 0,
						">" => left > right ? 1 : 0,
						"<=" => left <= right ? 1 : 0,
						">=" => left >= right ? 1 : 0,
						"<<" => left << (int)right,
						">>" => left >> (int)right,
						"+" => left + right,
						"-" => left - right,
						"*" => left * right,
						"/" => left / right,
						"%" => left % right,
						_ => throw new Exception("Invalid operator")
					};
				}
				return left;
			}

			private long ParseUnary()
			{
				string t = Peek;
				switch(t) {
					case "-": _pos++; return -ParseUnary();
					case "+": _pos++; return ParseUnary();
					case "!": _pos++; return ParseUnary() == 0 ? 1 : 0;
					case "~": _pos++; return ~ParseUnary();
					case "(":
						_pos++;
						if(_pos < _tokens.Count && (_primitiveTypes.Contains(Peek) || _qualifiers.Contains(Peek))) {
							//Cast, e.g (unsigned char)
							while(_pos < _tokens.Count && Peek != ")") {
								_pos++;
							}
							_pos++;
							return ParseUnary();
						}
						long value = ParseTernary();
						if(Peek != ")") {
							throw new Exception("Expected )");
						}
						_pos++;
						return value;
				}

				if(_pos >= _tokens.Count) {
					throw new Exception("Unexpected end of expression");
				}

				Tok tok = _tokens[_pos++];
				if(tok.Type == TokType.Identifier) {
					return 0;
				} else if(tok.Type == TokType.Number) {
					return ParseNumber(tok.Text);
				}
				throw new Exception("Unexpected token");
			}

			private static long ParseNumber(string text)
			{
				if(text.StartsWith("'")) {
					string content = text.Trim('\'');
					if(content.StartsWith("\\") && content.Length > 1) {
						return content[1] switch { 'n' => 10, 'r' => 13, 't' => 9, '0' => 0, _ => content[1] };
					}
					return content.Length > 0 ? content[0] : 0;
				}

				text = text.TrimEnd('u', 'U', 'l', 'L');
				if(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
					return long.Parse(text.Substring(2), NumberStyles.HexNumber);
				} else if(text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) {
					return Convert.ToInt64(text.Substring(2), 2);
				} else if(text.Length > 1 && text.StartsWith("0")) {
					return Convert.ToInt64(text, 8);
				}
				return long.Parse(text, CultureInfo.InvariantCulture);
			}
		}

		#endregion
	}
}
