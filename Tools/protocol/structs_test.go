package main

import (
	"strings"
	"testing"
)

func parseTestStructs(t *testing.T) map[string]Struct {
	t.Helper()
	structs, err := ParseStructs("testdata", "messages.go", "view.go")
	if err != nil {
		t.Fatalf("ParseStructs: %v", err)
	}
	return structs
}

func TestParseStructsReadsJsonNamesInDeclarationOrder(t *testing.T) {
	structs := parseTestStructs(t)

	login, ok := structs["LoginReq"]
	if !ok {
		t.Fatal("LoginReq was not parsed")
	}
	if len(login.Fields) != 2 {
		t.Fatalf("LoginReq field count = %d, want 2", len(login.Fields))
	}
	if login.Fields[0].JSONName != "player_name" {
		t.Errorf("field 0 = %q, want player_name", login.Fields[0].JSONName)
	}
	if login.Fields[1].JSONName != "reconnect_token" || !login.Fields[1].OmitEmpty {
		t.Errorf("field 1 = %+v, want reconnect_token with omitempty", login.Fields[1])
	}
	if login.Fields[0].Nullable {
		t.Error("a string field must not be nullable")
	}
}

func TestParseStructsKeepsEmptyStructsWithNoFields(t *testing.T) {
	structs := parseTestStructs(t)

	empty, ok := structs["EndActionReq"]
	if !ok {
		t.Fatal("EndActionReq was not parsed")
	}
	if len(empty.Fields) != 0 {
		t.Fatalf("EndActionReq field count = %d, want 0", len(empty.Fields))
	}
}

func TestParseStructsMarksPointerFieldsNullable(t *testing.T) {
	structs := parseTestStructs(t)

	card := structs["CardView"]
	points := card.Fields[1]
	if points.JSONName != "points" {
		t.Fatalf("field 1 = %q, want points", points.JSONName)
	}
	if points.GoType != "*int" || !points.Nullable || points.Repeated {
		t.Errorf("points = %+v, want *int nullable and not repeated", points)
	}

	rawPoints := card.Fields[2]
	if !rawPoints.Nullable || !rawPoints.OmitEmpty {
		t.Errorf("raw_points = %+v, want nullable with omitempty", rawPoints)
	}
}

func TestParseStructsDescribesSlicesMapsAndNamedRefs(t *testing.T) {
	structs := parseTestStructs(t)

	hand := structs["PlayerView"].Fields[0]
	if hand.GoType != "[]CardView" || !hand.Repeated || !hand.Nullable {
		t.Errorf("hand = %+v, want []CardView repeated and nullable", hand)
	}
	if hand.TypeRef != "CardView" {
		t.Errorf("hand.TypeRef = %q, want CardView", hand.TypeRef)
	}

	extra := structs["PlayerView"].Fields[1]
	if extra.GoType != "map[string]any" || !extra.Nullable || extra.TypeRef != "" {
		t.Errorf("extra_info = %+v, want map[string]any nullable with no TypeRef", extra)
	}

	characters := structs["GameConfigEv"].Fields[0]
	if characters.GoType != "[]map[string]any" || !characters.Repeated || characters.TypeRef != "" {
		t.Errorf("characters = %+v, want []map[string]any with no TypeRef", characters)
	}

	card1 := structs["ReviveReq"].Fields[0]
	if card1.GoType != "CardRef" || card1.TypeRef != "CardRef" || card1.Nullable {
		t.Errorf("card1 = %+v, want a non-nullable CardRef reference", card1)
	}
}

func TestParseStructsDescribesPointerToNamedStruct(t *testing.T) {
	// *PendingAttackView is the shape the real GameStateView actually uses.
	pending := parseTestStructs(t)["NestedShapes"].Fields[0]

	if pending.JSONName != "pending_attack" {
		t.Fatalf("field 0 = %q, want pending_attack", pending.JSONName)
	}
	if pending.GoType != "*PendingAttackView" {
		t.Errorf("GoType = %q, want *PendingAttackView", pending.GoType)
	}
	if pending.TypeRef != "PendingAttackView" {
		t.Errorf("TypeRef = %q, want PendingAttackView", pending.TypeRef)
	}
	if !pending.Nullable || pending.Repeated || !pending.OmitEmpty {
		t.Errorf("pending_attack = %+v, want nullable, not repeated, omitempty", pending)
	}
}

func TestParseStructsKeepsRepeatedThroughPointerAndSliceNesting(t *testing.T) {
	fields := parseTestStructs(t)["NestedShapes"].Fields

	pointerToSlice := fields[1]
	if pointerToSlice.JSONName != "pointer_to_hand" {
		t.Fatalf("field 1 = %q, want pointer_to_hand", pointerToSlice.JSONName)
	}
	if pointerToSlice.GoType != "*[]CardView" {
		t.Errorf("GoType = %q, want *[]CardView", pointerToSlice.GoType)
	}
	if !pointerToSlice.Repeated {
		t.Error("*[]CardView is still a JSON array; Repeated must survive the pointer")
	}
	if !pointerToSlice.Nullable || pointerToSlice.TypeRef != "CardView" {
		t.Errorf("pointer_to_hand = %+v, want nullable with TypeRef CardView", pointerToSlice)
	}

	sliceOfPointer := fields[2]
	if sliceOfPointer.GoType != "[]*CardView" {
		t.Errorf("GoType = %q, want []*CardView", sliceOfPointer.GoType)
	}
	if !sliceOfPointer.Repeated || !sliceOfPointer.Nullable {
		t.Errorf("slice_of_pointer = %+v, want repeated and nullable", sliceOfPointer)
	}
	if sliceOfPointer.TypeRef != "CardView" {
		t.Errorf("TypeRef = %q, want CardView", sliceOfPointer.TypeRef)
	}
}

func TestParseStructsSkipsUnexportedFields(t *testing.T) {
	fields := parseTestStructs(t)["NestedShapes"].Fields

	if len(fields) != 3 {
		t.Fatalf("field count = %d, want 3; encoding/json never serializes unexported fields", len(fields))
	}
	for _, field := range fields {
		if field.JSONName == "unexported" {
			t.Error("an unexported field must not reach the contract, tag or no tag")
		}
	}
}

func TestParseStructsRejectsUnsupportedFieldTypes(t *testing.T) {
	_, err := ParseStructs("testdata/unsupported", "messages.go")
	if err == nil {
		t.Fatal("expected an error for a field type that cannot be a JSON contract")
	}
	if !strings.Contains(err.Error(), "BadField") || !strings.Contains(err.Error(), "Events") {
		t.Errorf("error = %q, want it to name BadField and Events", err.Error())
	}
}
