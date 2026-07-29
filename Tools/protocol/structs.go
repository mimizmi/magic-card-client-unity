package main

import (
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"path/filepath"
	"reflect"
	"strconv"
	"strings"
)

// Field is one JSON-visible field of a Go protocol struct.
type Field struct {
	JSONName  string
	GoType    string
	TypeRef   string
	Repeated  bool
	Nullable  bool
	OmitEmpty bool
}

// Struct is one named Go struct with its JSON-visible fields in declaration
// order.
type Struct struct {
	Name   string
	Fields []Field
}

// ParseStructs reads the named files from sourceDir and returns every struct
// declaration keyed by type name.
func ParseStructs(sourceDir string, fileNames ...string) (map[string]Struct, error) {
	fileSet := token.NewFileSet()
	structs := make(map[string]Struct)

	for _, fileName := range fileNames {
		path := filepath.Join(sourceDir, fileName)
		file, err := parser.ParseFile(fileSet, path, nil, 0)
		if err != nil {
			return nil, fmt.Errorf("parse %s: %w", path, err)
		}
		for _, decl := range file.Decls {
			group, ok := decl.(*ast.GenDecl)
			if !ok || group.Tok != token.TYPE {
				continue
			}
			for _, spec := range group.Specs {
				typeSpec, ok := spec.(*ast.TypeSpec)
				if !ok {
					continue
				}
				structType, ok := typeSpec.Type.(*ast.StructType)
				if !ok {
					continue
				}
				parsed, err := parseStruct(typeSpec.Name.Name, structType)
				if err != nil {
					return nil, err
				}
				structs[parsed.Name] = parsed
			}
		}
	}

	if len(structs) == 0 {
		return nil, fmt.Errorf("no struct declarations found in %s", sourceDir)
	}
	return structs, nil
}

func parseStruct(name string, node *ast.StructType) (Struct, error) {
	result := Struct{Name: name, Fields: []Field{}}
	for _, astField := range node.Fields.List {
		if len(astField.Names) != 1 {
			return Struct{}, fmt.Errorf("%s: embedded or grouped fields are not supported", name)
		}
		// encoding/json never serializes unexported fields, tag or no tag, so
		// they are not part of the wire contract.
		if !astField.Names[0].IsExported() {
			continue
		}
		jsonName, omitEmpty, skip := parseJSONTag(astField)
		if skip {
			continue
		}
		if jsonName == "" {
			jsonName = astField.Names[0].Name
		}
		goType, typeRef, repeated, nullable, err := describeType(astField.Type)
		if err != nil {
			return Struct{}, fmt.Errorf("%s.%s: %w", name, astField.Names[0].Name, err)
		}
		result.Fields = append(result.Fields, Field{
			JSONName:  jsonName,
			GoType:    goType,
			TypeRef:   typeRef,
			Repeated:  repeated,
			Nullable:  nullable,
			OmitEmpty: omitEmpty,
		})
	}
	return result, nil
}

func parseJSONTag(field *ast.Field) (name string, omitEmpty bool, skip bool) {
	if field.Tag == nil {
		return "", false, false
	}
	raw, err := strconv.Unquote(field.Tag.Value)
	if err != nil {
		return "", false, false
	}
	tag := reflect.StructTag(raw).Get("json")
	if tag == "-" {
		return "", false, true
	}
	parts := strings.Split(tag, ",")
	for _, option := range parts[1:] {
		if option == "omitempty" {
			omitEmpty = true
		}
	}
	return parts[0], omitEmpty, false
}

// describeType renders a field's Go type and reports whether it can marshal to
// JSON null. typeRef is a candidate named-type reference; the fixture builder
// clears it when the name is not a parsed struct.
//
// An unhandled type expression is an error rather than a placeholder value. The
// fixture this feeds is generated once and then byte-compared forever after, so
// a wrong value would be baked into the baseline and treated as correct from
// then on. Refusing to describe what we do not understand is the only way the
// drift gate stays honest.
func describeType(expr ast.Expr) (goType, typeRef string, repeated, nullable bool, err error) {
	switch node := expr.(type) {
	case *ast.Ident:
		return node.Name, node.Name, false, false, nil
	case *ast.StarExpr:
		// A *[]T is still a JSON array once dereferenced, so the inner
		// repeated flag has to survive the pointer.
		inner, ref, innerRepeated, _, err := describeType(node.X)
		if err != nil {
			return "", "", false, false, err
		}
		return "*" + inner, ref, innerRepeated, true, nil
	case *ast.ArrayType:
		inner, ref, _, _, err := describeType(node.Elt)
		if err != nil {
			return "", "", false, false, err
		}
		return "[]" + inner, ref, true, true, nil
	case *ast.MapType:
		key, _, _, _, err := describeType(node.Key)
		if err != nil {
			return "", "", false, false, err
		}
		value, _, _, _, err := describeType(node.Value)
		if err != nil {
			return "", "", false, false, err
		}
		return "map[" + key + "]" + value, "", false, true, nil
	case *ast.InterfaceType:
		return "any", "", false, true, nil
	case *ast.SelectorExpr:
		pkg, _, _, _, err := describeType(node.X)
		if err != nil {
			return "", "", false, false, err
		}
		return pkg + "." + node.Sel.Name, "", false, false, nil
	default:
		return "", "", false, false, fmt.Errorf(
			"unsupported field type %T; it cannot be represented in a JSON wire contract", expr)
	}
}
