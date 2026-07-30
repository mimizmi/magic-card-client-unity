package main

import (
	"fmt"
	"go/ast"
	"go/parser"
	"go/token"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
)

// MessageConst is one entry of the authoritative msgid.go const block.
type MessageConst struct {
	ID        uint16
	GoConst   string
	Direction string
	Kind      string
}

// ParseMessageConsts reads msgid.go from sourceDir and returns every Msg*
// constant sorted by ascending id.
func ParseMessageConsts(sourceDir string) ([]MessageConst, error) {
	fileSet := token.NewFileSet()
	path := filepath.Join(sourceDir, "msgid.go")
	file, err := parser.ParseFile(fileSet, path, nil, parser.ParseComments)
	if err != nil {
		return nil, fmt.Errorf("parse %s: %w", path, err)
	}

	var result []MessageConst
	for _, decl := range file.Decls {
		group, ok := decl.(*ast.GenDecl)
		if !ok || group.Tok != token.CONST {
			continue
		}
		for _, spec := range group.Specs {
			value, ok := spec.(*ast.ValueSpec)
			if !ok {
				continue
			}
			if !anyMsgPrefixed(value.Names) {
				continue
			}
			if len(value.Names) != 1 || len(value.Values) != 1 {
				return nil, fmt.Errorf("%s: grouped or implicit-value message constants are not supported", identNames(value.Names))
			}
			name := value.Names[0].Name
			literal, ok := value.Values[0].(*ast.BasicLit)
			if !ok || literal.Kind != token.INT {
				return nil, fmt.Errorf("%s: message id is not an integer literal", name)
			}
			id, err := strconv.ParseUint(literal.Value, 0, 16)
			if err != nil {
				return nil, fmt.Errorf("%s: %w", name, err)
			}
			direction, err := parseDirection(name, value.Comment)
			if err != nil {
				return nil, err
			}
			result = append(result, MessageConst{
				ID:        uint16(id),
				GoConst:   name,
				Direction: direction,
				Kind:      parseKind(name),
			})
		}
	}

	if len(result) == 0 {
		return nil, fmt.Errorf("no Msg* constants found in %s", path)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].ID < result[j].ID })
	return result, nil
}

// anyMsgPrefixed reports whether any of names starts with "Msg". A ValueSpec
// with no Msg-prefixed name at all is unrelated to the message-id block (for
// example, a sibling const group in the same file) and must stay silently
// skipped rather than be treated as a validation failure.
func anyMsgPrefixed(names []*ast.Ident) bool {
	for _, n := range names {
		if strings.HasPrefix(n.Name, "Msg") {
			return true
		}
	}
	return false
}

// identNames renders a comma-separated list of identifier names, used to name
// the offending constant(s) in a grouped or implicit-value error.
func identNames(names []*ast.Ident) string {
	parts := make([]string, len(names))
	for i, n := range names {
		parts[i] = n.Name
	}
	return strings.Join(parts, ", ")
}

// parseDirection reads the S→C / C→S arrow from a constant's line comment.
// A missing arrow is an error rather than a silent default: the direction is
// contract information the Go compiler does not enforce.
func parseDirection(name string, comment *ast.CommentGroup) (string, error) {
	if comment == nil {
		return "", fmt.Errorf("%s: missing a direction comment", name)
	}
	text := comment.Text()
	switch {
	case strings.Contains(text, "S\u2192C"):
		return "server_to_client", nil
	case strings.Contains(text, "C\u2192S"):
		return "client_to_server", nil
	default:
		return "", fmt.Errorf("%s: comment has no S\u2192C or C\u2192S direction arrow", name)
	}
}

// parseKind derives the message category from the naming convention documented
// at the top of msgid.go. Resp is tested before Req because neither suffix is a
// substring of the other, but the order documents the intent.
func parseKind(name string) string {
	switch {
	case strings.HasSuffix(name, "Resp"):
		return "response"
	case strings.HasSuffix(name, "Req"):
		return "request"
	case strings.HasSuffix(name, "Ev"):
		return "event"
	default:
		return "system"
	}
}
