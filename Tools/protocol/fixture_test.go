package main

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"
)

// testNames covers every constant in testdata/msgid.go. buildFrom errors on a
// constant with no entry, so an incomplete table is itself a test case.
func testNames() map[uint16]string {
	return map[uint16]string{
		1:    "Ping",
		2:    "Pong",
		1001: "LoginRequest",
		1002: "LoginResponse",
		5001: "DamageEvent",
	}
}

func TestBuildFromAssignsPayloadShapes(t *testing.T) {
	doc, err := buildFrom("testdata", testNames(), map[string]string{
		"MsgPing":      "",
		"MsgPong":      "",
		"MsgLoginResp": "EndActionReq",
		"MsgDamageEv":  "GameConfigEv",
	})
	if err != nil {
		t.Fatalf("buildFrom: %v", err)
	}
	if len(doc.Messages) != 5 {
		t.Fatalf("message count = %d, want 5", len(doc.Messages))
	}

	byID := map[uint16]MessageDocument{}
	for _, message := range doc.Messages {
		byID[message.ID] = message
	}

	if byID[1].Payload.Shape != "none" || byID[1].GoType != "" {
		t.Errorf("Ping = %+v, want shape none with no go_type", byID[1])
	}
	if len(byID[1].Payload.Fields) != 0 {
		t.Errorf("Ping fields = %+v, want none", byID[1].Payload.Fields)
	}
	if byID[1002].Payload.Shape != "empty" || len(byID[1002].Payload.Fields) != 0 {
		t.Errorf("LoginResponse payload = %+v, want shape empty", byID[1002].Payload)
	}
	if byID[1001].Payload.Shape != "struct" || len(byID[1001].Payload.Fields) != 2 {
		t.Errorf("LoginRequest payload = %+v, want shape struct with 2 fields", byID[1001].Payload)
	}
	if byID[1001].Name != "LoginRequest" || byID[1001].GoType != "LoginReq" {
		t.Errorf("LoginRequest = %+v, want name LoginRequest and go_type LoginReq", byID[1001])
	}
	if byID[1001].Direction != "client_to_server" || byID[1001].Kind != "request" {
		t.Errorf("LoginRequest = %+v, want a client_to_server request", byID[1001])
	}
}

func TestBuildFromSortsMessagesByAscendingId(t *testing.T) {
	doc, err := buildFrom("testdata", testNames(), map[string]string{
		"MsgPing":      "",
		"MsgPong":      "",
		"MsgLoginResp": "EndActionReq",
		"MsgDamageEv":  "GameConfigEv",
	})
	if err != nil {
		t.Fatalf("buildFrom: %v", err)
	}
	for i := 1; i < len(doc.Messages); i++ {
		if doc.Messages[i-1].ID >= doc.Messages[i].ID {
			t.Fatalf("messages are not strictly ascending at index %d: %d then %d",
				i, doc.Messages[i-1].ID, doc.Messages[i].ID)
		}
	}
}

func TestBuildFromEmitsOnlyReachableNestedTypes(t *testing.T) {
	doc, err := buildFrom("testdata", testNames(), map[string]string{
		"MsgPing":      "",
		"MsgPong":      "",
		"MsgLoginReq":  "ReviveReq",
		"MsgLoginResp": "EndActionReq",
		"MsgDamageEv":  "GameConfigEv",
	})
	if err != nil {
		t.Fatalf("buildFrom: %v", err)
	}

	if _, ok := doc.Types["CardRef"]; !ok {
		t.Error("CardRef is referenced by ReviveReq and must appear in types")
	}
	if _, ok := doc.Types["CardView"]; ok {
		t.Error("CardView is unreachable here and must not appear in types")
	}
	if _, ok := doc.Types["ReviveReq"]; ok {
		t.Error("a message payload struct must not be duplicated into types")
	}
	if len(doc.Types) != 1 {
		t.Errorf("types = %v, want exactly CardRef", sortedTypeNames(doc.Types))
	}
}

func TestBuildFromWalksNestedTypesTransitively(t *testing.T) {
	// PlayerView references CardView, which must be pulled in even though no
	// message payload names CardView directly.
	doc, err := buildFrom("testdata", testNames(), map[string]string{
		"MsgPing":      "",
		"MsgPong":      "",
		"MsgLoginReq":  "PlayerView",
		"MsgLoginResp": "EndActionReq",
		"MsgDamageEv":  "GameConfigEv",
	})
	if err != nil {
		t.Fatalf("buildFrom: %v", err)
	}

	if _, ok := doc.Types["CardView"]; !ok {
		t.Errorf("CardView is reachable through PlayerView.Hand; types = %v",
			sortedTypeNames(doc.Types))
	}
	if _, ok := doc.Types["PlayerView"]; ok {
		t.Error("PlayerView is a payload here and must not also appear in types")
	}
}

func TestBuildFromRejectsAMissingCSharpName(t *testing.T) {
	incomplete := map[uint16]string{1: "Ping"}
	_, err := buildFrom("testdata", incomplete, map[string]string{
		"MsgPing":      "",
		"MsgPong":      "",
		"MsgLoginResp": "EndActionReq",
		"MsgDamageEv":  "GameConfigEv",
	})
	if err == nil {
		t.Fatal("expected an error when a constant has no C# name mapping")
	}
	if !strings.Contains(err.Error(), "MsgPong") {
		t.Errorf("error = %q, want it to name the unmapped constant MsgPong", err.Error())
	}
}

func TestBuildFromRejectsAMissingStruct(t *testing.T) {
	_, err := buildFrom("testdata", testNames(), map[string]string{
		"MsgPing":      "",
		"MsgPong":      "",
		"MsgLoginResp": "NoSuchStruct",
		"MsgDamageEv":  "GameConfigEv",
	})
	if err == nil {
		t.Fatal("expected an error when a payload struct does not exist")
	}
	if !strings.Contains(err.Error(), "NoSuchStruct") {
		t.Errorf("error = %q, want it to name the missing struct", err.Error())
	}
}

func TestBuildFromClearsTypeRefsThatAreNotStructs(t *testing.T) {
	doc, err := buildFrom("testdata", testNames(), map[string]string{
		"MsgPing":      "",
		"MsgPong":      "",
		"MsgLoginResp": "EndActionReq",
		"MsgDamageEv":  "GameConfigEv",
	})
	if err != nil {
		t.Fatalf("buildFrom: %v", err)
	}

	byID := map[uint16]MessageDocument{}
	for _, message := range doc.Messages {
		byID[message.ID] = message
	}

	// LoginReq's fields are plain strings; describeType offers "string" as a
	// candidate ref and the builder must drop it.
	for _, field := range byID[1001].Payload.Fields {
		if field.TypeRef != "" {
			t.Errorf("%s: TypeRef = %q, want it cleared for a non-struct type",
				field.JSONName, field.TypeRef)
		}
	}
}

func TestRenderIsDeterministicAndUnescaped(t *testing.T) {
	doc := Document{
		Version: "legacy-v1",
		Types: map[string]TypeDocument{
			"B": {Fields: []FieldDocument{}},
			"A": {Fields: []FieldDocument{}},
		},
	}

	first, err := Render(doc)
	if err != nil {
		t.Fatalf("Render: %v", err)
	}
	second, err := Render(doc)
	if err != nil {
		t.Fatalf("Render: %v", err)
	}
	if !bytes.Equal(first, second) {
		t.Fatal("Render is not deterministic")
	}
	if !bytes.HasSuffix(first, []byte("\n")) {
		t.Error("rendered output must end with a newline")
	}
	if bytes.Contains(first, []byte("\r")) {
		t.Error("rendered output must use LF only; the gate byte-compares it")
	}
	if bytes.Contains(first, []byte("\\u")) {
		t.Error("rendered output must not contain escaped characters")
	}
	if !json.Valid(first) {
		t.Error("rendered output is not valid JSON")
	}
	if bytes.Index(first, []byte(`"A"`)) > bytes.Index(first, []byte(`"B"`)) {
		t.Error("types keys must be emitted in sorted order")
	}
}

func TestRenderRoundTripsThroughTheDocumentModel(t *testing.T) {
	// The C# side reads this file with its own model. A field the Go struct
	// emits but cannot read back would silently diverge from what C# sees.
	original, err := buildFrom("testdata", testNames(), map[string]string{
		"MsgPing":      "",
		"MsgPong":      "",
		"MsgLoginReq":  "PlayerView",
		"MsgLoginResp": "EndActionReq",
		"MsgDamageEv":  "GameConfigEv",
	})
	if err != nil {
		t.Fatalf("buildFrom: %v", err)
	}

	rendered, err := Render(original)
	if err != nil {
		t.Fatalf("Render: %v", err)
	}

	var reparsed Document
	if err := json.Unmarshal(rendered, &reparsed); err != nil {
		t.Fatalf("Unmarshal: %v", err)
	}

	again, err := Render(reparsed)
	if err != nil {
		t.Fatalf("Render after round trip: %v", err)
	}
	if !bytes.Equal(rendered, again) {
		t.Error("the document model loses information on a JSON round trip")
	}
}
