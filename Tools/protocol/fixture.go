package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"sort"
	"strings"
)

// Document mirrors Packages/com.echo.harness/Fixtures/protocol.contract.json.
type Document struct {
	Version  string                  `json:"version"`
	Source   string                  `json:"source"`
	Frame    FrameDocument           `json:"frame"`
	Types    map[string]TypeDocument `json:"types"`
	Messages []MessageDocument       `json:"messages"`
}

type FrameDocument struct {
	ByteOrder               string `json:"byte_order"`
	LengthPrefixBytes       int    `json:"length_prefix_bytes"`
	MessageIDBytes          int    `json:"message_id_bytes"`
	LengthIncludesMessageID bool   `json:"length_includes_message_id"`
	BodyEncoding            string `json:"body_encoding"`
	MaxPayloadBytes         int    `json:"max_payload_bytes"`
}

type TypeDocument struct {
	Fields []FieldDocument `json:"fields"`
}

type MessageDocument struct {
	ID        uint16          `json:"id"`
	Name      string          `json:"name"`
	GoType    string          `json:"go_type,omitempty"`
	Direction string          `json:"direction"`
	Kind      string          `json:"kind"`
	Payload   PayloadDocument `json:"payload"`
}

type PayloadDocument struct {
	Shape  string          `json:"shape"`
	Fields []FieldDocument `json:"fields,omitempty"`
}

type FieldDocument struct {
	JSONName  string `json:"json_name"`
	GoType    string `json:"go_type"`
	TypeRef   string `json:"type_ref,omitempty"`
	Repeated  bool   `json:"repeated,omitempty"`
	Nullable  bool   `json:"nullable"`
	OmitEmpty bool   `json:"omitempty"`
}

// sourceLabel is a fixed literal, not the -source flag value. Deriving it from
// the flag would make the generated fixture depend on where the Go repository
// happens to be checked out, and the byte-comparison gate would then fail on
// any machine with a different layout.
const sourceLabel = "E:/code/_github/magic-card-server-golang/internal/protocol"

// frameBaseline is hard-coded. The real framing rules live in
// internal/network/codec.go, which this tool does not parse, so frame values
// are not drift-checked here. Tools/ci/verify-architecture.ps1 asserts each of
// them independently.
var frameBaseline = FrameDocument{
	ByteOrder:               "big_endian",
	LengthPrefixBytes:       4,
	MessageIDBytes:          2,
	LengthIncludesMessageID: false,
	BodyEncoding:            "utf-8-json",
	MaxPayloadBytes:         1048576,
}

// payloadOverrides handles the cases where the payload type is not the constant
// name with its Msg prefix removed. An empty value means the message carries no
// payload at all.
var payloadOverrides = map[string]string{
	"MsgGameStateEv":      "GameStateView",
	"MsgPing":             "",
	"MsgPong":             "",
	"MsgLeaveQueueReq":    "",
	"MsgRokkaActivateReq": "",
}

// csharpNames is hand-maintained: the C#-facing name cannot be derived from the
// Go source. ProtocolDtoContractTests cross-asserts it against the MessageId
// enum, so a missing or renamed entry fails the Unity suite.
var csharpNames = map[uint16]string{
	1: "Ping", 2: "Pong", 3: "ClientPingRequest", 4: "ClientPingResponse",
	1001: "LoginRequest", 1002: "LoginResponse",
	2001: "JoinQueueRequest", 2002: "JoinQueueResponse", 2003: "LeaveQueueRequest",
	2004: "MatchFoundEvent", 2005: "SelectCharacterRequest", 2006: "GameStartEvent",
	2007: "CreateAiGameRequest",
	3001: "GameStateEvent", 3002: "PhaseChangeEvent",
	4001: "PlayCardRequest", 4002: "MoveToSynthesisRequest", 4003: "SynthesizeRequest",
	4004: "UseSkillRequest", 4005: "TriggerLiberationRequest", 4006: "EndActionRequest",
	4007: "DefenseRequest", 4008: "GameConfigRequest", 4009: "SurrenderRequest",
	4010: "ReviveRequest", 4011: "RokkaActivateRequest",
	5001: "DamageEvent", 5002: "SkillUsedEvent", 5003: "LiberationEvent",
	5004: "FieldEffectEvent", 5005: "PlayerStatusEvent", 5006: "GameOverEvent",
	5007: "ErrorEvent", 5008: "BlessingEvent", 5009: "IncomingAttackEvent",
	5010: "TurnTimerEvent", 5011: "GameConfigEvent", 5012: "CardPlayedEvent",
	5013: "DeathDialogEvent",
}

// Build produces the fixture document from the authoritative Go source.
func Build(sourceDir string) (Document, error) {
	return buildFrom(sourceDir, csharpNames, payloadOverrides)
}

// buildFrom is Build with injectable tables so tests can drive the miniature
// testdata package.
func buildFrom(
	sourceDir string,
	names map[uint16]string,
	overrides map[string]string,
) (Document, error) {
	consts, err := ParseMessageConsts(sourceDir)
	if err != nil {
		return Document{}, err
	}
	structs, err := ParseStructs(sourceDir, "messages.go", "view.go")
	if err != nil {
		return Document{}, err
	}

	messages := make([]MessageDocument, 0, len(consts))
	payloadStructs := make(map[string]bool)

	for _, entry := range consts {
		name, ok := names[entry.ID]
		if !ok {
			return Document{}, fmt.Errorf(
				"%s (%d): no C# name mapping; add it to csharpNames", entry.GoConst, entry.ID)
		}

		goType := strings.TrimPrefix(entry.GoConst, "Msg")
		if override, ok := overrides[entry.GoConst]; ok {
			goType = override
		}

		payload := PayloadDocument{Shape: "none"}
		if goType != "" {
			parsed, ok := structs[goType]
			if !ok {
				return Document{}, fmt.Errorf(
					"%s: no struct %q in the Go source", entry.GoConst, goType)
			}
			payloadStructs[goType] = true
			if len(parsed.Fields) == 0 {
				payload = PayloadDocument{Shape: "empty"}
			} else {
				payload = PayloadDocument{
					Shape:  "struct",
					Fields: toFieldDocuments(parsed.Fields, structs),
				}
			}
		}

		messages = append(messages, MessageDocument{
			ID:        entry.ID,
			Name:      name,
			GoType:    goType,
			Direction: entry.Direction,
			Kind:      entry.Kind,
			Payload:   payload,
		})
	}

	return Document{
		Version:  "legacy-v1",
		Source:   sourceLabel,
		Frame:    frameBaseline,
		Types:    reachableTypes(messages, structs, payloadStructs),
		Messages: messages,
	}, nil
}

// toFieldDocuments converts parsed fields and clears any TypeRef that does not
// name a parsed struct. describeType offers the bare base identifier as a
// candidate, so "int" and "string" arrive here and must be dropped.
func toFieldDocuments(fields []Field, structs map[string]Struct) []FieldDocument {
	result := make([]FieldDocument, 0, len(fields))
	for _, field := range fields {
		typeRef := field.TypeRef
		if _, ok := structs[typeRef]; !ok {
			typeRef = ""
		}
		result = append(result, FieldDocument{
			JSONName:  field.JSONName,
			GoType:    field.GoType,
			TypeRef:   typeRef,
			Repeated:  field.Repeated,
			Nullable:  field.Nullable,
			OmitEmpty: field.OmitEmpty,
		})
	}
	return result
}

// reachableTypes walks every message payload and collects the named structs it
// references transitively, excluding structs that are themselves payloads.
func reachableTypes(
	messages []MessageDocument,
	structs map[string]Struct,
	payloadStructs map[string]bool,
) map[string]TypeDocument {
	result := make(map[string]TypeDocument)
	var queue []string

	for _, message := range messages {
		for _, field := range message.Payload.Fields {
			if field.TypeRef != "" {
				queue = append(queue, field.TypeRef)
			}
		}
	}

	for len(queue) > 0 {
		name := queue[0]
		queue = queue[1:]
		if payloadStructs[name] {
			continue
		}
		if _, done := result[name]; done {
			continue
		}
		parsed, ok := structs[name]
		if !ok {
			continue
		}
		fields := toFieldDocuments(parsed.Fields, structs)
		result[name] = TypeDocument{Fields: fields}
		for _, field := range fields {
			if field.TypeRef != "" {
				queue = append(queue, field.TypeRef)
			}
		}
	}

	return result
}

// Render serializes the document deterministically. encoding/json emits map
// keys in sorted order and every slice is already in a fixed order, so the same
// input always produces identical bytes. The output uses LF only; .gitattributes
// pins *.json to eol=lf so the byte-comparison gate cannot misfire on Windows.
func Render(doc Document) ([]byte, error) {
	var buffer bytes.Buffer
	encoder := json.NewEncoder(&buffer)
	encoder.SetEscapeHTML(false)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(doc); err != nil {
		return nil, fmt.Errorf("render fixture: %w", err)
	}
	return buffer.Bytes(), nil
}

// sortedTypeNames backs the diagnostic the CLI prints when the gate fails.
func sortedTypeNames(types map[string]TypeDocument) []string {
	names := make([]string, 0, len(types))
	for name := range types {
		names = append(names, name)
	}
	sort.Strings(names)
	return names
}
