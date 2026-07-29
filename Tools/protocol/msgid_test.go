package main

import "testing"

func TestParseMessageConstsReadsIdDirectionAndKind(t *testing.T) {
	consts, err := ParseMessageConsts("testdata")
	if err != nil {
		t.Fatalf("ParseMessageConsts: %v", err)
	}
	if len(consts) != 5 {
		t.Fatalf("len = %d, want 5", len(consts))
	}

	want := []MessageConst{
		{ID: 1, GoConst: "MsgPing", Direction: "server_to_client", Kind: "system"},
		{ID: 2, GoConst: "MsgPong", Direction: "client_to_server", Kind: "system"},
		{ID: 1001, GoConst: "MsgLoginReq", Direction: "client_to_server", Kind: "request"},
		{ID: 1002, GoConst: "MsgLoginResp", Direction: "server_to_client", Kind: "response"},
		{ID: 5001, GoConst: "MsgDamageEv", Direction: "server_to_client", Kind: "event"},
	}
	for i, expected := range want {
		if consts[i] != expected {
			t.Errorf("consts[%d] = %+v, want %+v", i, consts[i], expected)
		}
	}
}

func TestParseMessageConstsRejectsAMissingDirectionArrow(t *testing.T) {
	if _, err := ParseMessageConsts("testdata/nodirection"); err == nil {
		t.Fatal("expected an error for a constant with no direction arrow")
	}
}

func TestParseMessageConstsParsesHexIntegerLiteral(t *testing.T) {
	consts, err := ParseMessageConsts("testdata/hexid")
	if err != nil {
		t.Fatalf("ParseMessageConsts: %v", err)
	}
	want := []MessageConst{
		{ID: 1001, GoConst: "MsgHexId", Direction: "server_to_client", Kind: "system"},
	}
	if len(consts) != len(want) {
		t.Fatalf("len = %d, want %d", len(consts), len(want))
	}
	if consts[0] != want[0] {
		t.Errorf("consts[0] = %+v, want %+v", consts[0], want[0])
	}
}

func TestParseMessageConstsRejectsGroupedMessageConstants(t *testing.T) {
	if _, err := ParseMessageConsts("testdata/grouped"); err == nil {
		t.Fatal("expected an error for a grouped Msg constant declaration")
	}
}
