# Protocol Contract Typing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give all 39 Echo server message IDs a typed C# contract, and make Go-side JSON drift fail the build instead of relying on human review.

**Architecture:** A standalone Go program parses the authoritative Go protocol package with `go/ast` and generates `protocol.contract.json` with field-level schema. `verify-architecture.ps1` regenerates and byte-compares that fixture, so any Go JSON tag change fails the gate. Hand-written C# DTOs live in the existing `Echo.Harness.Contracts` assembly, and an EditMode suite asserts each DTO's declared JSON property names against the fixture.

**Tech Stack:** Go 1.25 (`go/parser`, `go/ast`, `encoding/json`), C# with Newtonsoft.Json for Unity 3.2.1, NUnit via Unity Test Framework 1.6.0, PowerShell 7.

Design spec: `docs/superpowers/specs/2026-07-29-protocol-contract-typing-design.md`.

## Global Constraints

- Unity editor stays pinned to `6000.2.7f2 (2b518236b676)`. Do not touch `ProjectSettings/ProjectVersion.txt`.
- **Do not add a new assembly definition.** `Tools/ci/verify-architecture.ps1` hard-codes the runtime assembly count and every assembly's reference set. All new C# goes into the existing `Echo.Harness.Contracts` assembly.
- Do not add or change any package pin in `Packages/manifest.json` or `Assets/packages.config`. The architecture gate asserts both sets exactly.
- The Go repository at `E:\code\_github\magic-card-server-golang` is authoritative and **read-only** for this work. Never edit it.
- Fixture `version` stays the literal string `legacy-v1`. Fixture `frame` values stay exactly: `big_endian`, `length_prefix_bytes` 4, `message_id_bytes` 2, `length_includes_message_id` false, `body_encoding` `utf-8-json`, `max_payload_bytes` 1048576.
- The fixture must contain exactly 39 messages with no duplicate ids.
- Every file created under `Packages/` needs its Unity-generated `.meta` file committed alongside it. Files under `Tools/` are outside Unity's import scope and have no `.meta`.
- C# DTO naming: `<fixture message name>Dto`, e.g. fixture name `DamageEvent` becomes `DamageEventDto`. Nested view types use their Go name plus `Dto`, e.g. `CardViewDto`.
- Tests are deterministic EditMode only. No sockets, no live server, no `Thread.Sleep`, no wall-clock dependence.
- **`omitempty` mapping depends on the C# type.** Use `NullValueHandling.Ignore` for reference types (`string`, `JObject`, nested DTOs) and `DefaultValueHandling.Ignore` for non-nullable value types (`int`, `bool`) — `NullValueHandling.Ignore` is a no-op on a type that can never be null. Never put `DefaultValueHandling.Ignore` on `int? Points` or `int? RawPoints`: it would omit a legitimate `0` alongside `null` and destroy the hidden-value distinction.

## File Structure

**Created — Go extractor (no `.meta`, outside Unity import scope):**

| File | Responsibility |
|---|---|
| `Tools/protocol/go.mod` | module declaration, Go 1.25 |
| `Tools/protocol/main.go` | CLI: `-source`, `-out`, `-check` |
| `Tools/protocol/msgid.go` | parse the `msgid.go` const block into id/direction/kind |
| `Tools/protocol/structs.go` | parse `messages.go` + `view.go` struct fields and JSON tags |
| `Tools/protocol/fixture.go` | document model, exception tables, C# name table, build, deterministic render |
| `Tools/protocol/msgid_test.go` | const-block parsing tests |
| `Tools/protocol/structs_test.go` | struct/field parsing tests |
| `Tools/protocol/fixture_test.go` | build + render + shape tests |
| `Tools/protocol/testdata/msgid.go` | hermetic miniature const block |
| `Tools/protocol/testdata/messages.go` | hermetic miniature message structs |
| `Tools/protocol/testdata/view.go` | hermetic miniature nested view structs |

`testdata` is a Go-reserved directory name; the toolchain will not try to build it as part of the module.

**Created — C# (each needs a committed `.meta`):**

| File | Responsibility |
|---|---|
| `Packages/com.echo.harness/Runtime/Contracts/Dtos/SystemDtos.cs` | ids 3-4 |
| `Packages/com.echo.harness/Runtime/Contracts/Dtos/AuthDtos.cs` | ids 1001-1002 |
| `Packages/com.echo.harness/Runtime/Contracts/Dtos/MatchmakingDtos.cs` | ids 2001-2007 |
| `Packages/com.echo.harness/Runtime/Contracts/Dtos/StateDtos.cs` | ids 3001-3002 and the view tree |
| `Packages/com.echo.harness/Runtime/Contracts/Dtos/CommandDtos.cs` | ids 4001-4011 and `CardRefDto` |
| `Packages/com.echo.harness/Runtime/Contracts/Dtos/EventDtos.cs` | ids 5001-5013 |
| `Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs` | `MessageId` to DTO `Type` registry |
| `Packages/com.echo.harness/Tests/EditMode/ProtocolDtoContractTests.cs` | fixture-driven contract assertions |
| `Packages/com.echo.harness/Tests/EditMode/ProtocolDtoSerializationTests.cs` | real JSON round-trip behavior, above all hidden-vs-zero points |

File families follow the id ranges already documented in `docs/protocol-contract.md`.

**Modified:**

| File | Change |
|---|---|
| `Packages/com.echo.harness/Fixtures/protocol.contract.json` | regenerated with `types` and per-message `payload` |
| `Packages/com.echo.harness/Runtime/Contracts/ProtocolContractFixture.cs` | model gains `Types`, `GoType`, `Payload`, field documents |
| `Packages/com.echo.harness/Runtime/Contracts/ProtocolDtos.cs` | **deleted**; its three DTOs and the `DamageEventDtoContract` helper move to `Dtos/EventDtos.cs` |
| `Packages/com.echo.harness/Tests/EditMode/ProtocolContractTests.cs` | two new frame tests appended |
| `Tools/ci/verify-architecture.ps1` | new `-GoServerRoot` parameter and the fixture drift gate |
| `Tools/ci/verify.ps1` | forwards `-GoServerRoot` to `verify-architecture.ps1` |
| `docs/protocol-contract.md` | documents the generated fixture and the new change procedure |
| `docs/verification-matrix.md` | new gate row |
| `docs/migration-checklist.md` | tick the typed-contract item |

## Reference: the 39 messages

Task implementers need this table. `shape` is the fixture's `payload.shape`.

| ID | Fixture name | Go type | Shape |
|---:|---|---|---|
| 1 | `Ping` | — | `none` |
| 2 | `Pong` | — | `none` |
| 3 | `ClientPingRequest` | `ClientPingReq` | `struct` |
| 4 | `ClientPingResponse` | `ClientPingResp` | `struct` |
| 1001 | `LoginRequest` | `LoginReq` | `struct` |
| 1002 | `LoginResponse` | `LoginResp` | `struct` |
| 2001 | `JoinQueueRequest` | `JoinQueueReq` | `struct` |
| 2002 | `JoinQueueResponse` | `JoinQueueResp` | `struct` |
| 2003 | `LeaveQueueRequest` | — | `none` |
| 2004 | `MatchFoundEvent` | `MatchFoundEv` | `struct` |
| 2005 | `SelectCharacterRequest` | `SelectCharacterReq` | `struct` |
| 2006 | `GameStartEvent` | `GameStartEv` | `struct` |
| 2007 | `CreateAiGameRequest` | `CreateAIGameReq` | `struct` |
| 3001 | `GameStateEvent` | `GameStateView` | `struct` |
| 3002 | `PhaseChangeEvent` | `PhaseChangeEv` | `struct` |
| 4001 | `PlayCardRequest` | `PlayCardReq` | `struct` |
| 4002 | `MoveToSynthesisRequest` | `MoveToSynthReq` | `struct` |
| 4003 | `SynthesizeRequest` | `SynthesizeReq` | `struct` |
| 4004 | `UseSkillRequest` | `UseSkillReq` | `struct` |
| 4005 | `TriggerLiberationRequest` | `TriggerLibrateReq` | `empty` |
| 4006 | `EndActionRequest` | `EndActionReq` | `empty` |
| 4007 | `DefenseRequest` | `DefenseReq` | `struct` |
| 4008 | `GameConfigRequest` | `GameConfigReq` | `empty` |
| 4009 | `SurrenderRequest` | `SurrenderReq` | `empty` |
| 4010 | `ReviveRequest` | `ReviveReq` | `struct` |
| 4011 | `RokkaActivateRequest` | — | `none` |
| 5001 | `DamageEvent` | `DamageEv` | `struct` |
| 5002 | `SkillUsedEvent` | `SkillUsedEv` | `struct` |
| 5003 | `LiberationEvent` | `LiberationEv` | `struct` |
| 5004 | `FieldEffectEvent` | `FieldEffectEv` | `struct` |
| 5005 | `PlayerStatusEvent` | `PlayerStatusEv` | `struct` |
| 5006 | `GameOverEvent` | `GameOverEv` | `struct` |
| 5007 | `ErrorEvent` | `ErrorEv` | `struct` |
| 5008 | `BlessingEvent` | `BlessingEv` | `struct` |
| 5009 | `IncomingAttackEvent` | `IncomingAttackEv` | `struct` |
| 5010 | `TurnTimerEvent` | `TurnTimerEv` | `struct` |
| 5011 | `GameConfigEvent` | `GameConfigEv` | `struct` |
| 5012 | `CardPlayedEvent` | `CardPlayedEv` | `struct` |
| 5013 | `DeathDialogEvent` | `DeathDialogEv` | `struct` |

Nested types emitted into the fixture's top-level `types`: `PendingAttackView`, `PlayerView`, `OpponentView`, `CardView`, `CardRef`.

## Known limitation, recorded deliberately

The fixture's `frame` block is a hard-coded constant in the extractor. The real framing rules live in `internal/network/codec.go`, not in the three files this tool parses, so `frame` is **not** drift-checked against Go. `verify-architecture.ps1` already asserts every `frame` value independently. Extending extraction to `codec.go` is future work and is out of scope here.

---

### Task 1: Extractor — parse the message id const block

**Files:**
- Create: `Tools/protocol/go.mod`
- Create: `Tools/protocol/msgid.go`
- Create: `Tools/protocol/testdata/msgid.go`
- Create: `Tools/protocol/testdata/nodirection/msgid.go`
- Test: `Tools/protocol/msgid_test.go`

**Interfaces:**
- Consumes: nothing.
- Produces: `type MessageConst struct { ID uint16; GoConst string; Direction string; Kind string }` and `func ParseMessageConsts(sourceDir string) ([]MessageConst, error)`. Returns entries sorted by ascending `ID`. `Direction` is `"client_to_server"` or `"server_to_client"`. `Kind` is `"system"`, `"request"`, `"response"`, or `"event"`.

> **Amendment (applied, commits `a60c284` and `3d38b3d`).** Review found two defects in the Step 5 code below, and the human partner ruled the review governs. The committed implementation therefore differs from the code block in this task; `Tools/protocol/msgid.go` is the source of truth. The two changes:
>
> 1. `strconv.ParseUint(literal.Value, 10, 16)` uses base **`0`**, not `10`. Base 10 rejects `0x3E9`, `0o1751`, `0b…`, and `1_001` — all legal `token.INT` literals — and would report them as "not an integer literal".
> 2. The single combined guard `if !ok || len(value.Names) != 1 || len(value.Values) != 1 { continue }` silently dropped `Msg`-prefixed constants declared as `MsgA, MsgB uint16 = 1, 2` or via implicit iota continuation. That contradicts this task's own stated principle that contract information the compiler does not enforce must error rather than default silently. It is now three ordered guards: a non-`ValueSpec` skips silently; a spec with no `Msg`-prefixed name skips silently (unrelated constants must not start erroring); a spec with any `Msg`-prefixed name but not exactly one name and one value returns an error naming the offending constants, via new `anyMsgPrefixed` and `identNames` helpers.
>
> Both error-path tests were also strengthened to assert the message names the offending constant, and `testdata/grouped/msgid.go` carries an unrelated valid constant so the grouped test discriminates — without it the pre-fix code errored anyway via the "no Msg* constants found" path, making the test tautological. A mutation check confirms the test fails against the pre-fix guard.

- [ ] **Step 1: Create the module file**

`Tools/protocol/go.mod`:

```
module echo/protocolcontract

go 1.25
```

- [ ] **Step 2: Create the hermetic test fixtures**

`Tools/protocol/testdata/msgid.go`. The arrow is U+2192 (`→`), the same character the real source uses. Save as UTF-8 without BOM.

```go
package protocol

const (
	MsgPing uint16 = 1 // S→C heartbeat probe
	MsgPong uint16 = 2 // C→S heartbeat response

	MsgLoginReq  uint16 = 1001 // C→S login
	MsgLoginResp uint16 = 1002 // S→C login result

	MsgDamageEv uint16 = 5001 // S→C damage detail
)
```

`Tools/protocol/testdata/nodirection/msgid.go`:

```go
package protocol

const (
	MsgMysteryEv uint16 = 9001 // no arrow here
)
```

- [ ] **Step 3: Write the failing test**

`Tools/protocol/msgid_test.go`:

```go
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
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `cd Tools/protocol && go test ./...`
Expected: FAIL — `undefined: ParseMessageConsts` and `undefined: MessageConst`.

- [ ] **Step 5: Implement the parser**

`Tools/protocol/msgid.go`:

```go
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
			if !ok || len(value.Names) != 1 || len(value.Values) != 1 {
				continue
			}
			name := value.Names[0].Name
			if !strings.HasPrefix(name, "Msg") {
				continue
			}
			literal, ok := value.Values[0].(*ast.BasicLit)
			if !ok || literal.Kind != token.INT {
				return nil, fmt.Errorf("%s: message id is not an integer literal", name)
			}
			id, err := strconv.ParseUint(literal.Value, 10, 16)
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
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `cd Tools/protocol && go test ./...`
Expected: PASS, 2 tests.

- [ ] **Step 7: Sanity-check against the real source**

Run:

```powershell
cd Tools/protocol
go vet ./...
```

Expected: no output, exit code 0. Full end-to-end verification against the real Go source happens in Task 3, once `Build` and the CLI exist.

- [ ] **Step 8: Commit**

```bash
git add Tools/protocol/go.mod Tools/protocol/msgid.go Tools/protocol/msgid_test.go Tools/protocol/testdata
git commit -m "Add protocol extractor message id parsing"
```

---

### Task 2: Extractor — parse struct fields and JSON tags

**Files:**
- Create: `Tools/protocol/structs.go`
- Create: `Tools/protocol/testdata/messages.go`
- Create: `Tools/protocol/testdata/view.go`
- Test: `Tools/protocol/structs_test.go`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `type Field struct { JSONName, GoType, TypeRef string; Repeated, Nullable, OmitEmpty bool }`, `type Struct struct { Name string; Fields []Field }`, and `func ParseStructs(sourceDir string, fileNames ...string) (map[string]Struct, error)`. `TypeRef` holds the *candidate* base identifier of the field type; Task 3 clears it when it is not a known struct name. Fields keep Go declaration order.

Nullability rule: `Nullable` is true exactly for Go types that can marshal to JSON `null` — pointers, slices, maps, and interfaces. `int`, `int64`, `bool`, and `string` are never nullable.

- [ ] **Step 1: Create the hermetic struct fixtures**

`Tools/protocol/testdata/messages.go`:

```go
package protocol

type LoginReq struct {
	PlayerName     string `json:"player_name"`
	ReconnectToken string `json:"reconnect_token,omitempty"`
}

type EndActionReq struct{}

type ReviveReq struct {
	Card1 CardRef `json:"card1"`
	Card2 CardRef `json:"card2"`
}

type CardRef struct {
	Zone string `json:"zone"`
	Slot int    `json:"slot"`
}

type GameConfigEv struct {
	Characters []map[string]any `json:"characters"`
	ConfigHash string           `json:"config_hash"`
}
```

`Tools/protocol/testdata/view.go`:

```go
package protocol

type CardView struct {
	Slot      int    `json:"slot"`
	Points    *int   `json:"points"`
	RawPoints *int   `json:"raw_points,omitempty"`
	Suit      string `json:"suit"`
}

type PlayerView struct {
	Hand      []CardView     `json:"hand"`
	ExtraInfo map[string]any `json:"extra_info,omitempty"`
}
```

- [ ] **Step 2: Write the failing test**

`Tools/protocol/structs_test.go`:

```go
package main

import "testing"

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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd Tools/protocol && go test ./...`
Expected: FAIL — `undefined: ParseStructs`, `undefined: Struct`, `undefined: Field`.

- [ ] **Step 4: Implement the struct parser**

`Tools/protocol/structs.go`:

```go
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
		jsonName, omitEmpty, skip := parseJSONTag(astField)
		if skip {
			continue
		}
		if jsonName == "" {
			jsonName = astField.Names[0].Name
		}
		goType, typeRef, repeated, nullable := describeType(astField.Type)
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
func describeType(expr ast.Expr) (goType, typeRef string, repeated, nullable bool) {
	switch node := expr.(type) {
	case *ast.Ident:
		return node.Name, node.Name, false, false
	case *ast.StarExpr:
		inner, ref, _, _ := describeType(node.X)
		return "*" + inner, ref, false, true
	case *ast.ArrayType:
		inner, ref, _, _ := describeType(node.Elt)
		return "[]" + inner, ref, true, true
	case *ast.MapType:
		key, _, _, _ := describeType(node.Key)
		value, _, _, _ := describeType(node.Value)
		return "map[" + key + "]" + value, "", false, true
	case *ast.InterfaceType:
		return "any", "", false, true
	case *ast.SelectorExpr:
		pkg, _, _, _ := describeType(node.X)
		return pkg + "." + node.Sel.Name, "", false, false
	default:
		return "unsupported", "", false, false
	}
}
```

Note: `any` is an `*ast.Ident` named `any`, so `map[string]any` renders through the `MapType` branch with an `Ident` value. `describeType` returns `"any"` as the candidate ref for that value, but the map branch discards it, which is why `extra_info` has no `TypeRef`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd Tools/protocol && go test ./...`
Expected: PASS, 6 tests.

- [ ] **Step 6: Commit**

```bash
git add Tools/protocol/structs.go Tools/protocol/structs_test.go Tools/protocol/testdata
git commit -m "Add protocol extractor struct and JSON tag parsing"
```

---

### Task 3: Extractor — build, render, and generate the real fixture

**Files:**
- Create: `Tools/protocol/fixture.go`
- Create: `Tools/protocol/main.go`
- Test: `Tools/protocol/fixture_test.go`
- Modify: `Packages/com.echo.harness/Fixtures/protocol.contract.json` (fully regenerated)

**Interfaces:**
- Consumes: `ParseMessageConsts` and `MessageConst` from Task 1; `ParseStructs`, `Struct`, `Field` from Task 2.
- Produces: `func Build(sourceDir string) (Document, error)`, `func Render(doc Document) ([]byte, error)`, `func sortedTypeNames(map[string]TypeDocument) []string`, and the document types listed in Step 3. The rendered bytes are what the gate compares.

- [ ] **Step 1: Write the failing test**

`Tools/protocol/fixture_test.go`. These tests drive the miniature `testdata` package through `buildFrom`, which takes injectable name and override tables.

```go
package main

import (
	"bytes"
	"encoding/json"
	"testing"
)

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
	if byID[1002].Payload.Shape != "empty" || len(byID[1002].Payload.Fields) != 0 {
		t.Errorf("LoginResponse payload = %+v, want shape empty", byID[1002].Payload)
	}
	if byID[1001].Payload.Shape != "struct" || len(byID[1001].Payload.Fields) != 2 {
		t.Errorf("LoginRequest payload = %+v, want shape struct with 2 fields", byID[1001].Payload)
	}
	if byID[1001].Name != "LoginRequest" {
		t.Errorf("LoginRequest name = %q", byID[1001].Name)
	}
	if byID[1001].Direction != "client_to_server" || byID[1001].Kind != "request" {
		t.Errorf("LoginRequest = %+v, want a client_to_server request", byID[1001])
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
}

func TestBuildFromRejectsAMissingCSharpName(t *testing.T) {
	_, err := buildFrom("testdata", map[uint16]string{1: "Ping"}, map[string]string{
		"MsgPing":      "",
		"MsgPong":      "",
		"MsgLoginReq":  "LoginReq",
		"MsgLoginResp": "EndActionReq",
		"MsgDamageEv":  "GameConfigEv",
	})
	if err == nil {
		t.Fatal("expected an error when a constant has no C# name mapping")
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd Tools/protocol && go test ./...`
Expected: FAIL — `undefined: buildFrom`, `undefined: Document`, `undefined: Render`.

- [ ] **Step 3: Implement the document model, tables, build, and render**

`Tools/protocol/fixture.go`:

```go
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

// payloadOverrides handles the four cases where the payload type is not the
// constant name with its Msg prefix removed. An empty value means the message
// carries no payload at all.
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
// name a parsed struct.
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
// keys in sorted order, and every slice is already in a fixed order, so the
// same input always produces identical bytes.
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
```

- [ ] **Step 4: Implement the CLI**

`Tools/protocol/main.go`:

```go
package main

import (
	"bytes"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

func main() {
	source := flag.String(
		"source",
		`E:\code\_github\magic-card-server-golang\internal\protocol`,
		"authoritative Go protocol package directory")
	out := flag.String("out", "", "write the generated fixture to this path")
	check := flag.String("check", "", "regenerate and byte-compare against this path")
	flag.Parse()

	if (*out == "") == (*check == "") {
		fmt.Fprintln(os.Stderr, "exactly one of -out or -check is required")
		os.Exit(2)
	}

	doc, err := Build(*source)
	if err != nil {
		fmt.Fprintln(os.Stderr, "extract:", err)
		os.Exit(1)
	}
	rendered, err := Render(doc)
	if err != nil {
		fmt.Fprintln(os.Stderr, "render:", err)
		os.Exit(1)
	}

	if *out != "" {
		if directory := filepath.Dir(*out); directory != "" {
			if err := os.MkdirAll(directory, 0o755); err != nil {
				fmt.Fprintln(os.Stderr, "mkdir:", err)
				os.Exit(1)
			}
		}
		if err := os.WriteFile(*out, rendered, 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "write:", err)
			os.Exit(1)
		}
		fmt.Printf("wrote %d messages and %d nested types to %s\n",
			len(doc.Messages), len(doc.Types), *out)
		return
	}

	existing, err := os.ReadFile(*check)
	if err != nil {
		fmt.Fprintln(os.Stderr, "read:", err)
		os.Exit(1)
	}
	if !bytes.Equal(existing, rendered) {
		fmt.Fprintf(os.Stderr,
			"protocol fixture is stale.\n  fixture: %s\n  source:  %s\n"+
				"  regenerated: %d messages, nested types: %s\n"+
				"Regenerate with: go run . -source <dir> -out <fixture>\n",
			*check, *source, len(doc.Messages), strings.Join(sortedTypeNames(doc.Types), ", "))
		os.Exit(1)
	}
	fmt.Printf("protocol fixture matches the Go source (%d messages)\n", len(doc.Messages))
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd Tools/protocol && go test ./...`
Expected: PASS, 11 tests.

- [ ] **Step 6: Confirm the current fixture is detected as stale**

Run:

```powershell
cd Tools/protocol
go run . -source 'E:\code\_github\magic-card-server-golang\internal\protocol' `
         -check '..\..\Packages\com.echo.harness\Fixtures\protocol.contract.json'
```

Expected: exit code 1 with `protocol fixture is stale`. This proves the gate detects drift before it is wired in.

If it instead reports `no C# name mapping` or `no struct ... in the Go source`, the extractor has found a real gap — fix the tables in `fixture.go` rather than working around the error.

- [ ] **Step 7: Generate the real fixture**

Run:

```powershell
cd Tools/protocol
go run . -source 'E:\code\_github\magic-card-server-golang\internal\protocol' `
         -out '..\..\Packages\com.echo.harness\Fixtures\protocol.contract.json'
```

Expected: `wrote 39 messages and 5 nested types to ...`

The counts must be exactly **39** and **5**. Then confirm the check now passes:

```powershell
go run . -source 'E:\code\_github\magic-card-server-golang\internal\protocol' `
         -check '..\..\Packages\com.echo.harness\Fixtures\protocol.contract.json'
```

Expected: `protocol fixture matches the Go source (39 messages)`, exit code 0.

- [ ] **Step 8: Confirm the existing architecture gate still passes**

Run: `.\Tools\ci\verify-architecture.ps1`
Expected: `Architecture verification passed.` The gate's `version`, `frame.*`, 39-message, and no-duplicate assertions all still apply to the regenerated file.

- [ ] **Step 9: Commit**

```bash
git add Tools/protocol/fixture.go Tools/protocol/main.go Tools/protocol/fixture_test.go \
        Packages/com.echo.harness/Fixtures/protocol.contract.json
git commit -m "Generate the protocol fixture from the Go source"
```

---

### Task 4: Wire the drift gate into verification

**Files:**
- Modify: `Tools/ci/verify-architecture.ps1:1-4` and the end of the file
- Modify: `Tools/ci/verify.ps1:19`

**Interfaces:**
- Consumes: the `-check` mode from Task 3.
- Produces: nothing consumed by later tasks.

The gate must **skip with a warning** when the Go source directory is absent. `.github/workflows/unity-tests.yml:17-25` runs `verify-architecture.ps1` on a `windows-latest` runner that does not check out the sibling Go repository, and `docs/verification-matrix.md` documents that CI boundary. A hard dependency would break the `architecture` job.

- [ ] **Step 1: Add the parameter to the architecture gate**

In `Tools/ci/verify-architecture.ps1`, replace the `param` block at lines 1-4:

```powershell
[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$GoServerRoot = 'E:\code\_github\magic-card-server-golang'
)
```

- [ ] **Step 2: Append the drift gate**

In `Tools/ci/verify-architecture.ps1`, insert immediately **before** the final `Write-Host 'Architecture verification passed.'` line:

```powershell
$FixturePath = Join-Path $ProjectRoot 'Packages\com.echo.harness\Fixtures\protocol.contract.json'
$GoProtocolSource = Join-Path $GoServerRoot 'internal\protocol'
if (Test-Path -LiteralPath $GoProtocolSource -PathType Container) {
    Push-Location -LiteralPath (Join-Path $ProjectRoot 'Tools\protocol')
    try {
        & go run . -source $GoProtocolSource -check $FixturePath
        if ($LASTEXITCODE -ne 0) {
            throw "The protocol fixture no longer matches $GoProtocolSource. Regenerate it with Tools/protocol -out."
        }
    } finally {
        Pop-Location
    }
} else {
    Write-Warning "Go protocol source not found at $GoProtocolSource; skipping the protocol fixture drift gate."
}
```

- [ ] **Step 3: Forward the parameter from the aggregate script**

In `Tools/ci/verify.ps1`, replace line 19:

```powershell
& (Join-Path $PSScriptRoot 'verify-architecture.ps1') -ProjectRoot $ProjectRoot
```

with:

```powershell
& (Join-Path $PSScriptRoot 'verify-architecture.ps1') `
    -ProjectRoot $ProjectRoot `
    -GoServerRoot $GoServerRoot
```

- [ ] **Step 4: Verify the gate passes with the Go source present**

Run: `.\Tools\ci\verify-architecture.ps1`
Expected: `protocol fixture matches the Go source (39 messages)` followed by `Architecture verification passed.`

- [ ] **Step 5: Verify the gate fails on real drift**

Temporarily corrupt the fixture, confirm the failure, then restore it:

```powershell
$fixture = 'Packages\com.echo.harness\Fixtures\protocol.contract.json'
(Get-Content -Raw $fixture).Replace('"attacker_seat"', '"seat"') |
    Set-Content -NoNewline $fixture
.\Tools\ci\verify-architecture.ps1
git checkout -- $fixture
```

Expected: the middle command throws `The protocol fixture no longer matches ...`. Confirm `git status --short` is clean afterwards.

- [ ] **Step 6: Verify the CI skip path**

Run: `.\Tools\ci\verify-architecture.ps1 -GoServerRoot 'E:\does-not-exist'`
Expected: a `WARNING: Go protocol source not found ...` line followed by `Architecture verification passed.`, exit code 0.

- [ ] **Step 7: Commit**

```bash
git add Tools/ci/verify-architecture.ps1 Tools/ci/verify.ps1
git commit -m "Gate the protocol fixture against the Go source"
```

---

### Task 5: Extend the C# fixture model and add the contract test harness

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Contracts/ProtocolContractFixture.cs`
- Create: `Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs`
- Create: `Packages/com.echo.harness/Runtime/Contracts/Dtos/SystemDtos.cs`
- Create: `Packages/com.echo.harness/Runtime/Contracts/Dtos/AuthDtos.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolDtoContractTests.cs`

**Interfaces:**
- Consumes: the fixture generated in Task 3.
- Produces:
  - `ProtocolMessageDocument.GoType` (string), `.Payload` (`ProtocolPayloadDocument`)
  - `ProtocolPayloadDocument { string Shape; List<ProtocolFieldDocument> Fields; }`
  - `ProtocolFieldDocument { string JsonName; string GoType; string TypeRef; bool Repeated; bool Nullable; bool OmitEmpty; }`
  - `ProtocolTypeDocument { List<ProtocolFieldDocument> Fields; }`
  - `ProtocolContractDocument.Types` (`Dictionary<string, ProtocolTypeDocument>`)
  - `ProtocolMessageMap.PayloadTypes` (`IReadOnlyDictionary<MessageId, Type>`) — Tasks 6, 7, and 8 add entries to this one dictionary initializer.

Tests read the **declared** Newtonsoft contract rather than serializing an instance. A property carrying `NullValueHandling.Ignore` disappears from a default instance's JSON, so serialization would under-report the contract. `DefaultContractResolver.ResolveContract` reports every declared property regardless.

- [ ] **Step 1: Extend the fixture model**

In `Packages/com.echo.harness/Runtime/Contracts/ProtocolContractFixture.cs`, add `Types` to `ProtocolContractDocument` after the `Frame` property:

```csharp
        [JsonProperty("types")]
        public Dictionary<string, ProtocolTypeDocument> Types { get; set; } =
            new Dictionary<string, ProtocolTypeDocument>();
```

Add `GoType` and `Payload` to `ProtocolMessageDocument` after the `Kind` property:

```csharp
        [JsonProperty("go_type")]
        public string GoType { get; set; } = string.Empty;

        [JsonProperty("payload")]
        public ProtocolPayloadDocument Payload { get; set; } = new ProtocolPayloadDocument();
```

Add these three classes to the same file, after `ProtocolMessageDocument`:

```csharp
    public sealed class ProtocolPayloadDocument
    {
        [JsonProperty("shape")]
        public string Shape { get; set; } = string.Empty;

        [JsonProperty("fields")]
        public List<ProtocolFieldDocument> Fields { get; set; } =
            new List<ProtocolFieldDocument>();
    }

    public sealed class ProtocolTypeDocument
    {
        [JsonProperty("fields")]
        public List<ProtocolFieldDocument> Fields { get; set; } =
            new List<ProtocolFieldDocument>();
    }

    public sealed class ProtocolFieldDocument
    {
        [JsonProperty("json_name")]
        public string JsonName { get; set; } = string.Empty;

        [JsonProperty("go_type")]
        public string GoType { get; set; } = string.Empty;

        [JsonProperty("type_ref")]
        public string TypeRef { get; set; } = string.Empty;

        [JsonProperty("repeated")]
        public bool Repeated { get; set; }

        [JsonProperty("nullable")]
        public bool Nullable { get; set; }

        [JsonProperty("omitempty")]
        public bool OmitEmpty { get; set; }
    }
```

- [ ] **Step 2: Write the failing test**

`Packages/com.echo.harness/Tests/EditMode/ProtocolDtoContractTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Echo.Harness.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolDtoContractTests
    {
        private static readonly DefaultContractResolver Resolver = new DefaultContractResolver();

        private static ProtocolContractDocument Fixture => ProtocolContractFixture.Load();

        private static IReadOnlyList<JsonProperty> DeclaredProperties(Type type)
        {
            var contract = (JsonObjectContract)Resolver.ResolveContract(type);
            return contract.Properties.ToArray();
        }

        [Test]
        public void FixtureNames_MatchTheMessageIdEnum()
        {
            foreach (var message in Fixture.Messages)
            {
                var id = (MessageId)message.Id;
                Assert.That(
                    Enum.GetName(typeof(MessageId), id),
                    Is.EqualTo(message.Name),
                    $"Fixture name for id {message.Id} does not match the MessageId enum.");
            }
        }

        [Test]
        public void RegisteredDtos_DeclareExactlyTheFixtureFieldNames()
        {
            foreach (var message in Fixture.Messages)
            {
                if (!ProtocolMessageMap.PayloadTypes.TryGetValue((MessageId)message.Id, out var type))
                {
                    continue;
                }

                var declared = DeclaredProperties(type).Select(property => property.PropertyName);
                var expected = message.Payload.Fields.Select(field => field.JsonName);

                Assert.That(
                    declared,
                    Is.EquivalentTo(expected),
                    $"{type.Name} does not match the fixture contract for {message.Name}.");
            }
        }

        [Test]
        public void RegisteredDtos_UseNullableTypesForNullableFields()
        {
            foreach (var message in Fixture.Messages)
            {
                if (!ProtocolMessageMap.PayloadTypes.TryGetValue((MessageId)message.Id, out var type))
                {
                    continue;
                }

                AssertNullability(type, message.Payload.Fields, message.Name);
            }
        }

        [Test]
        public void EmptyPayloadMessages_SerializeToAnEmptyObject()
        {
            foreach (var message in Fixture.Messages.Where(m => m.Payload.Shape == "empty"))
            {
                Assert.That(
                    ProtocolMessageMap.PayloadTypes.ContainsKey((MessageId)message.Id),
                    Is.True,
                    $"{message.Name} has an empty payload and still needs a registered DTO.");

                var type = ProtocolMessageMap.PayloadTypes[(MessageId)message.Id];
                Assert.That(DeclaredProperties(type), Is.Empty, $"{type.Name} must declare no properties.");
                Assert.That(
                    JsonConvert.SerializeObject(Activator.CreateInstance(type)),
                    Is.EqualTo("{}"),
                    $"{type.Name} must serialize to an empty JSON object.");
            }
        }

        [Test]
        public void NoPayloadMessages_HaveNoRegisteredDto()
        {
            foreach (var message in Fixture.Messages.Where(m => m.Payload.Shape == "none"))
            {
                Assert.That(
                    ProtocolMessageMap.PayloadTypes.ContainsKey((MessageId)message.Id),
                    Is.False,
                    $"{message.Name} carries no payload and must not have a registered DTO.");
            }
        }

        private static void AssertNullability(
            Type type,
            IReadOnlyList<ProtocolFieldDocument> fields,
            string context)
        {
            var declared = DeclaredProperties(type)
                .ToDictionary(property => property.PropertyName, property => property);

            foreach (var field in fields)
            {
                Assert.That(
                    declared.ContainsKey(field.JsonName),
                    Is.True,
                    $"{context}: {type.Name} is missing '{field.JsonName}'.");

                var propertyType = declared[field.JsonName].PropertyType;
                var isNullable =
                    !propertyType.IsValueType || Nullable.GetUnderlyingType(propertyType) != null;

                if (field.Nullable)
                {
                    Assert.That(
                        isNullable,
                        Is.True,
                        $"{context}: '{field.JsonName}' is nullable in Go ({field.GoType}) " +
                        $"and must be a nullable C# type, not {propertyType.Name}.");
                }
            }
        }
    }
}
```

- [ ] **Step 3: Create the System DTOs**

`Packages/com.echo.harness/Runtime/Contracts/Dtos/SystemDtos.cs`:

```csharp
using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    /// <summary>Message 3 - client-initiated latency probe.</summary>
    public sealed class ClientPingRequestDto
    {
        [JsonProperty("ts")]
        public long Ts { get; set; }
    }

    /// <summary>Message 4 - the server echoes the client timestamp back.</summary>
    public sealed class ClientPingResponseDto
    {
        [JsonProperty("ts")]
        public long Ts { get; set; }
    }
}
```

Messages 1 and 2 (Ping, Pong) carry a nil payload and deliberately have no DTO.

- [ ] **Step 4: Create the Auth DTOs**

`Packages/com.echo.harness/Runtime/Contracts/Dtos/AuthDtos.cs`:

```csharp
using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    /// <summary>Message 1001 - first login or reconnect.</summary>
    public sealed class LoginRequestDto
    {
        [JsonProperty("player_name")]
        public string PlayerName { get; set; } = string.Empty;

        [JsonProperty("reconnect_token", NullValueHandling = NullValueHandling.Ignore)]
        public string ReconnectToken { get; set; }
    }

    /// <summary>Message 1002 - login result.</summary>
    public sealed class LoginResponseDto
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("player_id", NullValueHandling = NullValueHandling.Ignore)]
        public string PlayerId { get; set; }

        [JsonProperty("reconnect_token", NullValueHandling = NullValueHandling.Ignore)]
        public string ReconnectToken { get; set; }

        // bool can never be null, so NullValueHandling.Ignore would be a no-op.
        // DefaultValueHandling.Ignore reproduces Go's omitempty on a value type.
        [JsonProperty("in_game", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool InGame { get; set; }

        [JsonProperty("config_hash", NullValueHandling = NullValueHandling.Ignore)]
        public string ConfigHash { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }
    }
}
```

- [ ] **Step 5: Create the message map**

`Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Echo.Harness.Contracts
{
    /// <summary>
    /// Maps each message id to its typed payload. Messages whose fixture payload
    /// shape is "none" (Ping, Pong, LeaveQueueRequest, RokkaActivateRequest)
    /// are deliberately absent.
    /// </summary>
    public static class ProtocolMessageMap
    {
        public static IReadOnlyDictionary<MessageId, Type> PayloadTypes { get; } =
            new Dictionary<MessageId, Type>
            {
                { MessageId.ClientPingRequest, typeof(ClientPingRequestDto) },
                { MessageId.ClientPingResponse, typeof(ClientPingResponseDto) },
                { MessageId.LoginRequest, typeof(LoginRequestDto) },
                { MessageId.LoginResponse, typeof(LoginResponseDto) },
            };
    }
}
```

- [ ] **Step 6: Refresh Unity and run the tests**

Use the connected Unity MCP editor: call `recompile`, wait for `recompile_status` to report completion, then `run_tests` with mode `editor` and filter `Echo.Harness.Tests.EditMode.ProtocolDtoContractTests`.

Expected: all 5 tests PASS. `NoPayloadMessages_HaveNoRegisteredDto` and `FixtureNames_MatchTheMessageIdEnum` already cover all 39 messages; the other three cover only the 4 registered DTOs so far.

If `run_tests` is unavailable, run `.\Tools\ci\run-unity-tests.ps1` instead.

- [ ] **Step 7: Commit with the generated .meta files**

Unity generates a `.meta` for every new file and directory under `Packages/`. Confirm they exist before committing:

```bash
git status --short Packages/com.echo.harness/
git add Packages/com.echo.harness/Runtime/Contracts/ \
        Packages/com.echo.harness/Tests/EditMode/
git commit -m "Add the fixture-driven DTO contract harness with system and auth DTOs"
```

Every added `.cs` must have its `.cs.meta` staged alongside it, and the new `Dtos` directory needs its `Dtos.meta`. If `git status` shows a `.cs` without its `.meta`, Unity has not finished importing — re-run `recompile` and wait.

---

### Task 6: Matchmaking and command DTOs

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Contracts/Dtos/MatchmakingDtos.cs`
- Create: `Packages/com.echo.harness/Runtime/Contracts/Dtos/CommandDtos.cs`
- Modify: `Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs`

**Interfaces:**
- Consumes: `ProtocolMessageMap.PayloadTypes` from Task 5.
- Produces: `CardRefDto`, registered as a nested type by Task 8. All other types here are consumed only by tests.

- [ ] **Step 1: Create the matchmaking DTOs**

`Packages/com.echo.harness/Runtime/Contracts/Dtos/MatchmakingDtos.cs`:

```csharp
using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    /// <summary>Message 2001 - join the matchmaking queue.</summary>
    public sealed class JoinQueueRequestDto
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; } = string.Empty;
    }

    /// <summary>Message 2002 - queue join result.</summary>
    public sealed class JoinQueueResponseDto
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }
    }

    /// <summary>Message 2004 - a match was found; character selection begins.</summary>
    public sealed class MatchFoundEventDto
    {
        [JsonProperty("game_id")]
        public string GameId { get; set; } = string.Empty;

        [JsonProperty("your_seat")]
        public int YourSeat { get; set; }

        [JsonProperty("opponent_name")]
        public string OpponentName { get; set; } = string.Empty;
    }

    /// <summary>Message 2005 - select a character face-down.</summary>
    public sealed class SelectCharacterRequestDto
    {
        [JsonProperty("character_id")]
        public string CharacterId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message 2006 - both players have selected. Seat characters are "???"
    /// until a skill reveals them.
    /// </summary>
    public sealed class GameStartEventDto
    {
        [JsonProperty("game_id")]
        public string GameId { get; set; } = string.Empty;

        [JsonProperty("seat0_char")]
        public string Seat0Char { get; set; } = string.Empty;

        [JsonProperty("seat1_char")]
        public string Seat1Char { get; set; } = string.Empty;
    }

    /// <summary>Message 2007 - create an AI match without queueing.</summary>
    public sealed class CreateAiGameRequestDto
    {
        [JsonProperty("player_char_id")]
        public string PlayerCharId { get; set; } = string.Empty;

        [JsonProperty("ai_char_id")]
        public string AiCharId { get; set; } = string.Empty;
    }
}
```

Message 2003 (`LeaveQueueRequest`) has no Go struct and deliberately has no DTO.

- [ ] **Step 2: Create the command DTOs**

`Packages/com.echo.harness/Runtime/Contracts/Dtos/CommandDtos.cs`:

```csharp
using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    /// <summary>References a card in the hand or synthesis zone.</summary>
    public sealed class CardRefDto
    {
        [JsonProperty("zone")]
        public string Zone { get; set; } = string.Empty;

        [JsonProperty("slot")]
        public int Slot { get; set; }
    }

    /// <summary>Message 4001 - play a card. Zone is "hand" or "synth".</summary>
    public sealed class PlayCardRequestDto
    {
        [JsonProperty("zone")]
        public string Zone { get; set; } = string.Empty;

        [JsonProperty("slot")]
        public int Slot { get; set; }
    }

    /// <summary>Message 4002 - move a hand card into the synthesis zone.</summary>
    public sealed class MoveToSynthesisRequestDto
    {
        [JsonProperty("hand_slot")]
        public int HandSlot { get; set; }

        // Go: `target_slot,omitempty` with 0 meaning auto. int is a value type,
        // so DefaultValueHandling.Ignore is what reproduces omitempty here.
        [JsonProperty("target_slot", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int TargetSlot { get; set; }
    }

    /// <summary>Message 4003 - synthesize two cards.</summary>
    public sealed class SynthesizeRequestDto
    {
        [JsonProperty("slot1")]
        public int Slot1 { get; set; }

        [JsonProperty("zone1")]
        public string Zone1 { get; set; } = string.Empty;

        [JsonProperty("slot2")]
        public int Slot2 { get; set; }

        [JsonProperty("zone2")]
        public string Zone2 { get; set; } = string.Empty;
    }

    /// <summary>Message 4004 - use an active skill.</summary>
    public sealed class UseSkillRequestDto
    {
        [JsonProperty("skill_card_slot")]
        public int SkillCardSlot { get; set; }
    }

    /// <summary>Message 4005 - manually trigger liberation. Empty payload.</summary>
    public sealed class TriggerLiberationRequestDto
    {
    }

    /// <summary>Message 4006 - end the action phase. Empty payload.</summary>
    public sealed class EndActionRequestDto
    {
    }

    /// <summary>Message 4007 - respond to an incoming attack.</summary>
    public sealed class DefenseRequestDto
    {
        [JsonProperty("pass")]
        public bool Pass { get; set; }

        [JsonProperty("zone", NullValueHandling = NullValueHandling.Ignore)]
        public string Zone { get; set; }

        // Go: `slot,omitempty` on an int. See the omitempty constraint.
        [JsonProperty("slot", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Slot { get; set; }
    }

    /// <summary>Message 4008 - request the full game config. Empty payload.</summary>
    public sealed class GameConfigRequestDto
    {
    }

    /// <summary>Message 4009 - surrender. Empty payload.</summary>
    public sealed class SurrenderRequestDto
    {
    }

    /// <summary>Message 4010 - Suou revival: submit two cards.</summary>
    public sealed class ReviveRequestDto
    {
        [JsonProperty("card1")]
        public CardRefDto Card1 { get; set; }

        [JsonProperty("card2")]
        public CardRefDto Card2 { get; set; }
    }
}
```

Message 4011 (`RokkaActivateRequest`) has no Go struct and deliberately has no DTO.

- [ ] **Step 3: Register the new DTOs**

In `Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs`, add these entries to the dictionary initializer after the existing four:

```csharp
                { MessageId.JoinQueueRequest, typeof(JoinQueueRequestDto) },
                { MessageId.JoinQueueResponse, typeof(JoinQueueResponseDto) },
                { MessageId.MatchFoundEvent, typeof(MatchFoundEventDto) },
                { MessageId.SelectCharacterRequest, typeof(SelectCharacterRequestDto) },
                { MessageId.GameStartEvent, typeof(GameStartEventDto) },
                { MessageId.CreateAiGameRequest, typeof(CreateAiGameRequestDto) },
                { MessageId.PlayCardRequest, typeof(PlayCardRequestDto) },
                { MessageId.MoveToSynthesisRequest, typeof(MoveToSynthesisRequestDto) },
                { MessageId.SynthesizeRequest, typeof(SynthesizeRequestDto) },
                { MessageId.UseSkillRequest, typeof(UseSkillRequestDto) },
                { MessageId.TriggerLiberationRequest, typeof(TriggerLiberationRequestDto) },
                { MessageId.EndActionRequest, typeof(EndActionRequestDto) },
                { MessageId.DefenseRequest, typeof(DefenseRequestDto) },
                { MessageId.GameConfigRequest, typeof(GameConfigRequestDto) },
                { MessageId.SurrenderRequest, typeof(SurrenderRequestDto) },
                { MessageId.ReviveRequest, typeof(ReviveRequestDto) },
```

- [ ] **Step 4: Refresh Unity and run the tests**

Call Unity MCP `recompile`, wait for `recompile_status`, then `run_tests` with mode `editor` and filter `ProtocolDtoContractTests`.

Expected: all 5 tests PASS, now covering 20 registered DTOs. `EmptyPayloadMessages_SerializeToAnEmptyObject` now exercises all four empty-payload messages.

- [ ] **Step 5: Commit**

```bash
git status --short Packages/com.echo.harness/
git add Packages/com.echo.harness/Runtime/Contracts/
git commit -m "Add matchmaking and command DTOs"
```

---

### Task 7: Event DTOs and the retirement of ProtocolDtos.cs

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Contracts/Dtos/EventDtos.cs`
- Delete: `Packages/com.echo.harness/Runtime/Contracts/ProtocolDtos.cs` and its `.meta`
- Modify: `Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs`

**Interfaces:**
- Consumes: `ProtocolMessageMap.PayloadTypes` from Task 6.
- Produces: `DamageEventDto`, `LiberationEventDto`, `FieldEffectEventDto`, and the `DamageEventDtoContract` helper — all moved from `ProtocolDtos.cs` with **unchanged names**, so `ProtocolContractTests.DamageEvent_UsesAuthoritativeGoJsonNames` keeps compiling and passing.

- [ ] **Step 1: Create the event DTOs**

`Packages/com.echo.harness/Runtime/Contracts/Dtos/EventDtos.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Echo.Harness.Contracts
{
    /// <summary>
    /// Message 5001 - damage settlement detail. The Go names are authoritative;
    /// the legacy Godot client used seat/amount/damage_type and was wrong.
    /// </summary>
    public sealed class DamageEventDto
    {
        [JsonProperty("attacker_seat")]
        public int AttackerSeat { get; set; }

        [JsonProperty("defender_seat")]
        public int DefenderSeat { get; set; }

        [JsonProperty("raw_damage")]
        public int RawDamage { get; set; }

        [JsonProperty("final_damage")]
        public int FinalDamage { get; set; }

        [JsonProperty("hp_after")]
        public int HpAfter { get; set; }

        [JsonProperty("detail")]
        public string Detail { get; set; } = string.Empty;
    }

    /// <summary>Message 5002 - a skill was used, which also reveals the character.</summary>
    public sealed class SkillUsedEventDto
    {
        [JsonProperty("player_seat")]
        public int PlayerSeat { get; set; }

        [JsonProperty("character")]
        public string Character { get; set; } = string.Empty;

        [JsonProperty("skill_level")]
        public int SkillLevel { get; set; }

        [JsonProperty("desc")]
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>Message 5003 - liberation triggered. Go uses player_seat, not seat.</summary>
    public sealed class LiberationEventDto
    {
        [JsonProperty("player_seat")]
        public int PlayerSeat { get; set; }

        [JsonProperty("character")]
        public string Character { get; set; } = string.Empty;

        [JsonProperty("desc")]
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>Message 5004 - field effect applied. Go uses three fields, not field_effect.</summary>
    public sealed class FieldEffectEventDto
    {
        [JsonProperty("effect_id")]
        public string EffectId { get; set; } = string.Empty;

        [JsonProperty("effect_name")]
        public string EffectName { get; set; } = string.Empty;

        [JsonProperty("desc")]
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>Message 5005 - incremental HP and energy update.</summary>
    public sealed class PlayerStatusEventDto
    {
        [JsonProperty("seat")]
        public int Seat { get; set; }

        [JsonProperty("hp")]
        public int Hp { get; set; }

        [JsonProperty("max_hp")]
        public int MaxHp { get; set; }

        [JsonProperty("shield_hp")]
        public int ShieldHp { get; set; }

        [JsonProperty("energy")]
        public int Energy { get; set; }

        [JsonProperty("max_energy")]
        public int MaxEnergy { get; set; }
    }

    /// <summary>Message 5006 - the game ended.</summary>
    public sealed class GameOverEventDto
    {
        [JsonProperty("winner_seat")]
        public int WinnerSeat { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>Message 5007 - a non-fatal operation error. The connection stays open.</summary>
    public sealed class ErrorEventDto
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Message 5008 - blessing triggered below 40 HP; a second character is granted.</summary>
    public sealed class BlessingEventDto
    {
        [JsonProperty("player_seat")]
        public int PlayerSeat { get; set; }

        [JsonProperty("second_char_id")]
        public string SecondCharId { get; set; } = string.Empty;

        [JsonProperty("second_char_name")]
        public string SecondCharName { get; set; } = string.Empty;
    }

    /// <summary>Message 5009 - an attack is incoming and the defense window is open.</summary>
    public sealed class IncomingAttackEventDto
    {
        [JsonProperty("attacker_seat")]
        public int AttackerSeat { get; set; }

        [JsonProperty("attack_points")]
        public int AttackPoints { get; set; }
    }

    /// <summary>Message 5010 - action countdown, pushed once per second.</summary>
    public sealed class TurnTimerEventDto
    {
        [JsonProperty("active_seat")]
        public int ActiveSeat { get; set; }

        [JsonProperty("seconds_left")]
        public int SecondsLeft { get; set; }
    }

    /// <summary>
    /// Message 5011 - character and field data. The server side is map[string]any
    /// with no fixed schema, so these stay opaque rather than inventing one.
    /// </summary>
    public sealed class GameConfigEventDto
    {
        [JsonProperty("characters")]
        public IReadOnlyList<JObject> Characters { get; set; }

        [JsonProperty("fields")]
        public IReadOnlyList<JObject> Fields { get; set; }

        [JsonProperty("config_hash")]
        public string ConfigHash { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message 5012 - a card was played, visible to both players. Points is
    /// nullable: null means the point value is hidden.
    /// </summary>
    public sealed class CardPlayedEventDto
    {
        [JsonProperty("player_seat")]
        public int PlayerSeat { get; set; }

        [JsonProperty("card_type")]
        public string CardType { get; set; } = string.Empty;

        [JsonProperty("suit")]
        public string Suit { get; set; } = string.Empty;

        [JsonProperty("points")]
        public int? Points { get; set; }
    }

    /// <summary>Message 5013 - Suou hit zero HP and entered the 15s revival dialog.</summary>
    public sealed class DeathDialogEventDto
    {
        [JsonProperty("seat")]
        public int Seat { get; set; }

        [JsonProperty("deadline_ms")]
        public long DeadlineMs { get; set; }

        [JsonProperty("duration_sec")]
        public int DurationSec { get; set; }
    }

    /// <summary>Reports the wire names a damage event actually serializes.</summary>
    public static class DamageEventDtoContract
    {
        public static IReadOnlyList<string> SerializePropertyNames(DamageEventDto dto)
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(dto));
            return json.Properties().Select(property => property.Name).ToArray();
        }
    }
}
```

- [ ] **Step 2: Delete the superseded file**

```bash
git rm Packages/com.echo.harness/Runtime/Contracts/ProtocolDtos.cs \
       Packages/com.echo.harness/Runtime/Contracts/ProtocolDtos.cs.meta
```

- [ ] **Step 3: Register the event DTOs**

In `ProtocolMessageMap.cs`, append to the dictionary initializer:

```csharp
                { MessageId.DamageEvent, typeof(DamageEventDto) },
                { MessageId.SkillUsedEvent, typeof(SkillUsedEventDto) },
                { MessageId.LiberationEvent, typeof(LiberationEventDto) },
                { MessageId.FieldEffectEvent, typeof(FieldEffectEventDto) },
                { MessageId.PlayerStatusEvent, typeof(PlayerStatusEventDto) },
                { MessageId.GameOverEvent, typeof(GameOverEventDto) },
                { MessageId.ErrorEvent, typeof(ErrorEventDto) },
                { MessageId.BlessingEvent, typeof(BlessingEventDto) },
                { MessageId.IncomingAttackEvent, typeof(IncomingAttackEventDto) },
                { MessageId.TurnTimerEvent, typeof(TurnTimerEventDto) },
                { MessageId.GameConfigEvent, typeof(GameConfigEventDto) },
                { MessageId.CardPlayedEvent, typeof(CardPlayedEventDto) },
                { MessageId.DeathDialogEvent, typeof(DeathDialogEventDto) },
```

- [ ] **Step 4: Refresh Unity and run the full EditMode suite**

Call Unity MCP `recompile`, wait for `recompile_status`, then `run_tests` with mode `editor` and no filter.

Expected: every EditMode test passes, including the pre-existing `ProtocolContractTests.DamageEvent_UsesAuthoritativeGoJsonNames`, which still resolves `DamageEventDtoContract` from its new home.

- [ ] **Step 5: Commit**

```bash
git status --short Packages/com.echo.harness/
git add Packages/com.echo.harness/Runtime/Contracts/
git commit -m "Add event DTOs and retire ProtocolDtos.cs"
```

---

### Task 8: State DTOs and the completeness gate

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Contracts/Dtos/StateDtos.cs`
- Create: `Packages/com.echo.harness/Tests/EditMode/ProtocolDtoSerializationTests.cs`
- Modify: `Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs`
- Modify: `Packages/com.echo.harness/Tests/EditMode/ProtocolDtoContractTests.cs`

**Interfaces:**
- Consumes: `ProtocolMessageMap.PayloadTypes` from Task 7; `CardRefDto` from Task 6; `ProtocolContractDocument.Types` from Task 5.
- Produces: `PendingAttackViewDto`, `PlayerViewDto`, `OpponentViewDto`, `CardViewDto`, and `ProtocolMessageMap.NestedTypes`. After this task every fixture message with a payload has a registered DTO.

This task carries the correctness core of the iteration: `CardViewDto.Points` and `.RawPoints` are `int?`. A null means the server is **hiding** the point value. Collapsing null into `0` would defeat information hiding.

- [ ] **Step 1: Create the state DTOs**

`Packages/com.echo.harness/Runtime/Contracts/Dtos/StateDtos.cs`:

```csharp
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Echo.Harness.Contracts
{
    /// <summary>
    /// One card as presented to a specific player. A null Points means the
    /// server is hiding the value; it must never be treated as zero.
    /// </summary>
    public sealed class CardViewDto
    {
        [JsonProperty("slot")]
        public int Slot { get; set; }

        [JsonProperty("suit")]
        public string Suit { get; set; } = string.Empty;

        [JsonProperty("card_type")]
        public string CardType { get; set; } = string.Empty;

        [JsonProperty("points")]
        public int? Points { get; set; }

        [JsonProperty("raw_points", NullValueHandling = NullValueHandling.Ignore)]
        public int? RawPoints { get; set; }
    }

    /// <summary>An open defense window against a pending attack.</summary>
    public sealed class PendingAttackViewDto
    {
        [JsonProperty("attacker_seat")]
        public int AttackerSeat { get; set; }

        [JsonProperty("attack_points")]
        public int AttackPoints { get; set; }
    }

    /// <summary>The full information a player has about themselves.</summary>
    public sealed class PlayerViewDto
    {
        [JsonProperty("seat")]
        public int Seat { get; set; }

        [JsonProperty("hp")]
        public int Hp { get; set; }

        [JsonProperty("max_hp")]
        public int MaxHp { get; set; }

        [JsonProperty("shield_hp")]
        public int ShieldHp { get; set; }

        [JsonProperty("energy")]
        public int Energy { get; set; }

        [JsonProperty("max_energy")]
        public int MaxEnergy { get; set; }

        [JsonProperty("character")]
        public string Character { get; set; } = string.Empty;

        [JsonProperty("is_near_death")]
        public bool IsNearDeath { get; set; }

        [JsonProperty("hand")]
        public IReadOnlyList<CardViewDto> Hand { get; set; }

        [JsonProperty("synth_zone")]
        public IReadOnlyList<CardViewDto> SynthZone { get; set; }

        [JsonProperty("extra_info", NullValueHandling = NullValueHandling.Ignore)]
        public JObject ExtraInfo { get; set; }
    }

    /// <summary>
    /// The restricted information a player has about the opponent. There is no
    /// Hand property by design: opponent hand contents are never sent.
    /// </summary>
    public sealed class OpponentViewDto
    {
        [JsonProperty("seat")]
        public int Seat { get; set; }

        [JsonProperty("hp")]
        public int Hp { get; set; }

        [JsonProperty("max_hp")]
        public int MaxHp { get; set; }

        [JsonProperty("shield_hp")]
        public int ShieldHp { get; set; }

        [JsonProperty("energy")]
        public int Energy { get; set; }

        [JsonProperty("max_energy")]
        public int MaxEnergy { get; set; }

        [JsonProperty("character")]
        public string Character { get; set; } = string.Empty;

        [JsonProperty("is_near_death")]
        public bool IsNearDeath { get; set; }

        [JsonProperty("hand_count")]
        public int HandCount { get; set; }

        [JsonProperty("synth_count")]
        public int SynthCount { get; set; }

        [JsonProperty("public_extra", NullValueHandling = NullValueHandling.Ignore)]
        public JObject PublicExtra { get; set; }
    }

    /// <summary>Message 3001 - the player-specific authoritative state snapshot.</summary>
    public sealed class GameStateEventDto
    {
        [JsonProperty("round")]
        public int Round { get; set; }

        [JsonProperty("phase")]
        public string Phase { get; set; } = string.Empty;

        [JsonProperty("active_seat")]
        public int ActiveSeat { get; set; }

        [JsonProperty("field_effect")]
        public string FieldEffect { get; set; } = string.Empty;

        [JsonProperty("pending_attack", NullValueHandling = NullValueHandling.Ignore)]
        public PendingAttackViewDto PendingAttack { get; set; }

        [JsonProperty("me")]
        public PlayerViewDto Me { get; set; }

        [JsonProperty("opponent")]
        public OpponentViewDto Opponent { get; set; }
    }

    /// <summary>Message 3002 - phase transition notice.</summary>
    public sealed class PhaseChangeEventDto
    {
        [JsonProperty("round")]
        public int Round { get; set; }

        [JsonProperty("phase")]
        public string Phase { get; set; } = string.Empty;

        [JsonProperty("active_seat")]
        public int ActiveSeat { get; set; }

        [JsonProperty("field_effect")]
        public string FieldEffect { get; set; } = string.Empty;
    }
}
```

- [ ] **Step 2: Register the state DTOs and the nested types**

In `ProtocolMessageMap.cs`, append to the `PayloadTypes` dictionary initializer:

```csharp
                { MessageId.GameStateEvent, typeof(GameStateEventDto) },
                { MessageId.PhaseChangeEvent, typeof(PhaseChangeEventDto) },
```

Then add this property to `ProtocolMessageMap`, after `PayloadTypes`. Nested view types are not messages, so they need their own registry keyed by the Go type names the extractor emits into the fixture's `types` dictionary:

```csharp
        /// <summary>
        /// Maps each fixture "types" entry to its DTO. Keys are the Go type
        /// names the extractor emits.
        /// </summary>
        public static IReadOnlyDictionary<string, Type> NestedTypes { get; } =
            new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                { "CardRef", typeof(CardRefDto) },
                { "CardView", typeof(CardViewDto) },
                { "PendingAttackView", typeof(PendingAttackViewDto) },
                { "PlayerView", typeof(PlayerViewDto) },
                { "OpponentView", typeof(OpponentViewDto) },
            };
```

`ProtocolMessageMap.cs` already has `using System;` from Task 5, which covers `StringComparer`.

- [ ] **Step 3: Add the completeness and nested-type tests**

Append these two tests inside `ProtocolDtoContractTests`, immediately before the private `AssertNullability` helper:

```csharp
        [Test]
        public void EveryMessageWithAPayload_HasARegisteredDto()
        {
            var missing = Fixture.Messages
                .Where(message => message.Payload.Shape != "none")
                .Where(message => !ProtocolMessageMap.PayloadTypes.ContainsKey((MessageId)message.Id))
                .Select(message => $"{message.Id} {message.Name}")
                .ToArray();

            Assert.That(missing, Is.Empty, "These messages still need a typed DTO.");
        }

        [Test]
        public void EveryNestedType_HasARegisteredDtoMatchingTheFixture()
        {
            Assert.That(
                ProtocolMessageMap.NestedTypes.Keys,
                Is.EquivalentTo(Fixture.Types.Keys),
                "The nested type registry and the fixture types disagree.");

            foreach (var entry in Fixture.Types)
            {
                var type = ProtocolMessageMap.NestedTypes[entry.Key];
                var declared = DeclaredProperties(type).Select(property => property.PropertyName);
                var expected = entry.Value.Fields.Select(field => field.JsonName);

                Assert.That(
                    declared,
                    Is.EquivalentTo(expected),
                    $"{type.Name} does not match the fixture contract for {entry.Key}.");

                AssertNullability(type, entry.Value.Fields, entry.Key);
            }
        }

        [Test]
        public void FieldsWithATypeRef_UseTheMatchingNestedDto()
        {
            foreach (var message in Fixture.Messages)
            {
                if (ProtocolMessageMap.PayloadTypes.TryGetValue((MessageId)message.Id, out var type))
                {
                    AssertTypeRefs(type, message.Payload.Fields, message.Name);
                }
            }

            foreach (var entry in Fixture.Types)
            {
                AssertTypeRefs(ProtocolMessageMap.NestedTypes[entry.Key], entry.Value.Fields, entry.Key);
            }
        }
```

Add these two private helpers alongside `AssertNullability`:

```csharp
        private static void AssertTypeRefs(
            Type type,
            IReadOnlyList<ProtocolFieldDocument> fields,
            string context)
        {
            var declared = DeclaredProperties(type)
                .ToDictionary(property => property.PropertyName, property => property);

            foreach (var field in fields)
            {
                if (string.IsNullOrEmpty(field.TypeRef))
                {
                    continue;
                }

                Assert.That(
                    ProtocolMessageMap.NestedTypes.ContainsKey(field.TypeRef),
                    Is.True,
                    $"{context}: '{field.JsonName}' references unknown nested type '{field.TypeRef}'.");

                var expected = ProtocolMessageMap.NestedTypes[field.TypeRef];
                var actual = ElementType(declared[field.JsonName].PropertyType);

                Assert.That(
                    actual,
                    Is.EqualTo(expected),
                    $"{context}: '{field.JsonName}' is {field.GoType} in Go and must map to " +
                    $"{expected.Name}, not {actual.Name}.");
            }
        }

        /// <summary>
        /// Unwraps IReadOnlyList&lt;T&gt; so a repeated field compares against its
        /// element type. Non-collection types are returned unchanged.
        /// </summary>
        private static Type ElementType(Type type)
        {
            if (type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            {
                return type.GetGenericArguments()[0];
            }

            return type;
        }
```

This is the assertion that catches a structurally wrong mapping. Without it, declaring `GameStateEventDto.Me` as a `string` would still pass the name-set and nullability checks.

- [ ] **Step 4: Refresh Unity and run the full EditMode suite**

Call Unity MCP `recompile`, wait for `recompile_status`, then `run_tests` with mode `editor`.

Expected: every test passes. `EveryMessageWithAPayload_HasARegisteredDto` now covers all 35 payload-carrying messages, and `EveryNestedType_HasARegisteredDtoMatchingTheFixture` covers all 5 nested types.

If `EveryNestedType_...` fails on the key set, the fixture's `types` differ from the 5 expected names. Re-read the Task 3 Step 7 output line reporting the nested type count before changing the C# side — the fixture is generated and is the source of truth here.

- [ ] **Step 5: Add the JSON round-trip tests**

Everything so far inspects declared contract *metadata*. Nothing has executed a
real serialization. These tests exercise the behavior the metadata only
describes — above all that a hidden point value survives as `null` and never
collapses to `0`.

Create `Packages/com.echo.harness/Tests/EditMode/ProtocolDtoSerializationTests.cs`:

```csharp
using Echo.Harness.Contracts;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolDtoSerializationTests
    {
        // U+2665 heart suit and U+653B U+51FB ("attack" card type), written as
        // escapes so the test file's own encoding cannot corrupt them.
        private const string HeartSuit = "♥";
        private const string AttackCardType = "攻击";

        [Test]
        public void CardView_HiddenPointsDeserializeToNullNotZero()
        {
            var dto = JsonConvert.DeserializeObject<CardViewDto>(
                "{\"slot\":3,\"suit\":\"" + HeartSuit + "\"," +
                "\"card_type\":\"" + AttackCardType + "\",\"points\":null}");

            Assert.That(dto.Slot, Is.EqualTo(3));
            Assert.That(dto.Suit, Is.EqualTo(HeartSuit));
            Assert.That(dto.CardType, Is.EqualTo(AttackCardType));
            Assert.That(dto.Points, Is.Null, "A null points value means hidden and must never become 0.");
            Assert.That(dto.RawPoints, Is.Null, "An absent raw_points must stay null.");
        }

        [Test]
        public void CardView_ZeroPointsStaysDistinctFromHidden()
        {
            var visible = JsonConvert.DeserializeObject<CardViewDto>(
                "{\"slot\":1,\"suit\":\"" + HeartSuit + "\",\"card_type\":\"x\",\"points\":0}");
            var hidden = JsonConvert.DeserializeObject<CardViewDto>(
                "{\"slot\":1,\"suit\":\"" + HeartSuit + "\",\"card_type\":\"x\",\"points\":null}");

            Assert.That(visible.Points, Is.EqualTo(0));
            Assert.That(hidden.Points, Is.Null);
            Assert.That(visible.Points, Is.Not.EqualTo(hidden.Points),
                "Zero points and hidden points must remain distinguishable.");
        }

        [Test]
        public void GameStateEvent_DeserializesTheWholeNestedTree()
        {
            const string json =
                "{\"round\":2,\"phase\":\"action\",\"active_seat\":1,\"field_effect\":\"\"," +
                "\"pending_attack\":{\"attacker_seat\":0,\"attack_points\":7}," +
                "\"me\":{\"seat\":1,\"hp\":30,\"max_hp\":50,\"shield_hp\":0,\"energy\":4," +
                "\"max_energy\":10,\"character\":\"???\",\"is_near_death\":false," +
                "\"hand\":[{\"slot\":1,\"suit\":\"" + HeartSuit + "\",\"card_type\":\"x\"," +
                "\"points\":5,\"raw_points\":3}]," +
                "\"synth_zone\":[],\"extra_info\":{\"rift_count\":2}}," +
                "\"opponent\":{\"seat\":0,\"hp\":40,\"max_hp\":50,\"shield_hp\":2,\"energy\":1," +
                "\"max_energy\":10,\"character\":\"???\",\"is_near_death\":false," +
                "\"hand_count\":6,\"synth_count\":1}}";

            var dto = JsonConvert.DeserializeObject<GameStateEventDto>(json);

            Assert.That(dto.PendingAttack, Is.Not.Null);
            Assert.That(dto.PendingAttack.AttackPoints, Is.EqualTo(7));
            Assert.That(dto.Me.Hand, Has.Count.EqualTo(1),
                "IReadOnlyList<CardViewDto> must deserialize.");
            Assert.That(dto.Me.Hand[0].Points, Is.EqualTo(5));
            Assert.That(dto.Me.Hand[0].RawPoints, Is.EqualTo(3));
            Assert.That(dto.Me.Hand[0].Suit, Is.EqualTo(HeartSuit));
            Assert.That(dto.Me.SynthZone, Is.Empty);
            Assert.That((int)dto.Me.ExtraInfo["rift_count"], Is.EqualTo(2));
            Assert.That(dto.Opponent.HandCount, Is.EqualTo(6));
            Assert.That(dto.Opponent.PublicExtra, Is.Null,
                "An absent public_extra must stay null.");
        }

        [Test]
        public void GameStateEvent_AbsentPendingAttackMeansNoDefenseWindow()
        {
            var dto = JsonConvert.DeserializeObject<GameStateEventDto>(
                "{\"round\":1,\"phase\":\"draw\",\"active_seat\":0,\"field_effect\":\"\"}");

            Assert.That(dto.PendingAttack, Is.Null);
        }

        [Test]
        public void MoveToSynthesisRequest_OmitsTheDefaultTargetSlot()
        {
            Assert.That(
                JsonConvert.SerializeObject(new MoveToSynthesisRequestDto { HandSlot = 2 }),
                Is.EqualTo("{\"hand_slot\":2}"),
                "target_slot carries omitempty in Go and must vanish at its default.");

            Assert.That(
                JsonConvert.SerializeObject(
                    new MoveToSynthesisRequestDto { HandSlot = 2, TargetSlot = 3 }),
                Is.EqualTo("{\"hand_slot\":2,\"target_slot\":3}"));
        }

        [Test]
        public void MoveToSynthesisRequest_KeepsAZeroHandSlot()
        {
            Assert.That(
                JsonConvert.SerializeObject(new MoveToSynthesisRequestDto()),
                Is.EqualTo("{\"hand_slot\":0}"),
                "hand_slot has no omitempty in Go and must always be sent.");
        }

        [Test]
        public void DefenseRequest_OmitsZoneAndSlotWhenPassing()
        {
            Assert.That(
                JsonConvert.SerializeObject(new DefenseRequestDto { Pass = true }),
                Is.EqualTo("{\"pass\":true}"));
        }

        [Test]
        public void LoginRequest_OmitsANullReconnectToken()
        {
            Assert.That(
                JsonConvert.SerializeObject(new LoginRequestDto { PlayerName = "echo" }),
                Is.EqualTo("{\"player_name\":\"echo\"}"));
        }
    }
}
```

If `GameStateEvent_DeserializesTheWholeNestedTree` fails on `Me.Hand`, Newtonsoft
cannot materialize `IReadOnlyList<CardViewDto>` in this configuration. Change the
DTO property to `IReadOnlyList<CardViewDto>` backed by a `List<CardViewDto>`
setter only if that is genuinely the cause — verify with the actual exception
before changing the contract shape.

- [ ] **Step 6: Run the full EditMode suite again**

Call Unity MCP `recompile`, wait for `recompile_status`, then `run_tests` with mode `editor`.

Expected: every test passes, including the 8 new serialization tests.

- [ ] **Step 7: Commit**

```bash
git status --short Packages/com.echo.harness/
git add Packages/com.echo.harness/Runtime/Contracts/ \
        Packages/com.echo.harness/Tests/EditMode/
git commit -m "Add state DTOs, the completeness gate, and JSON round-trip tests"
```

---

### Task 9: Frame edge cases

**Files:**
- Modify: `Packages/com.echo.harness/Tests/EditMode/ProtocolContractTests.cs`

**Interfaces:**
- Consumes: `BinaryFrameCodec` and `WireFrameSpec` — already present, unchanged.
- Produces: nothing.

Two contract facts have no coverage today. The Go server sends `s.Send(MsgIDPing, nil)`, a zero-length payload, and card suits are the Unicode symbols `♥ ♦ ♣ ♠`, which must survive a UTF-8 round trip.

- [ ] **Step 1: Write the tests**

Append these two tests inside `ProtocolContractTests`. The file already imports `System`, `System.Linq`, and `System.Text`.

```csharp
        [Test]
        public void BinaryFrame_AcceptsAZeroLengthPayload()
        {
            var encoded = BinaryFrameCodec.Encode(MessageId.Ping, Array.Empty<byte>());

            Assert.That(encoded, Has.Length.EqualTo(
                WireFrameSpec.LengthPrefixBytes + WireFrameSpec.MessageIdBytes));
            Assert.That(encoded.Take(4).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 0 }));

            var decoded = BinaryFrameCodec.Decode(encoded);

            Assert.That(decoded.MessageId, Is.EqualTo(MessageId.Ping));
            Assert.That(decoded.Payload.Length, Is.EqualTo(0));
        }

        [Test]
        public void BinaryFrame_RoundTripsUnicodeSuitSymbols()
        {
            const string body = "{\"suit\":\"\u2665\",\"suits\":[\"\u2666\",\"\u2663\",\"\u2660\"]}";
            var payload = Encoding.UTF8.GetBytes(body);

            var decoded = BinaryFrameCodec.Decode(
                BinaryFrameCodec.Encode(MessageId.CardPlayedEvent, payload));

            Assert.That(Encoding.UTF8.GetString(decoded.Payload.ToArray()), Is.EqualTo(body));
        }
```

The escapes are `\u2665` heart, `\u2666` diamond, `\u2663` club, `\u2660` spade. They are written as escapes so the test file's own encoding cannot corrupt them.

- [ ] **Step 2: Run the tests**

Call Unity MCP `recompile`, wait for `recompile_status`, then `run_tests` with mode `editor` and filter `ProtocolContractTests`.

Expected: both new tests PASS. `BinaryFrameCodec` already handles both cases; these tests lock that behavior in against future transport work. If either fails, that is a real defect in `BinaryFrameCodec` — fix the codec, not the test.

- [ ] **Step 3: Commit**

```bash
git add Packages/com.echo.harness/Tests/EditMode/ProtocolContractTests.cs
git commit -m "Cover zero-length payloads and Unicode suit round-trips"
```

---

### Task 10: Documentation and full verification

**Files:**
- Modify: `docs/protocol-contract.md:58-69`
- Modify: `docs/verification-matrix.md:15-21` and `:50-55`
- Modify: `docs/migration-checklist.md:41`

**Interfaces:**
- Consumes: everything from Tasks 1-9.
- Produces: nothing.

- [ ] **Step 1: Document the generated fixture**

In `docs/protocol-contract.md`, replace the entire "Change procedure" section (from the `## Change procedure` heading to the end of the file) with:

````markdown
## Change procedure

1. Change or confirm the Go type and JSON tag first.
2. Regenerate the fixture:

```powershell
cd Tools/protocol
go run . -source 'E:\code\_github\magic-card-server-golang\internal\protocol' `
         -out '..\..\Packages\com.echo.harness\Fixtures\protocol.contract.json'
```

3. Add or update the typed DTO and register it in `ProtocolMessageMap`.
4. Run `Tools/ci/verify.ps1`.
5. Review hidden-information impact: each client must receive only its permitted
   player-specific view.
6. Version the protocol before introducing an incompatible production change.

`protocol.contract.json` is **generated**, not hand-edited. `Tools/protocol`
parses `msgid.go`, `messages.go`, and `view.go` with `go/ast` and emits the
document deterministically; `verify-architecture.ps1` regenerates it and
byte-compares against the committed file, so any Go JSON tag change fails the
gate.

Two things the extractor does not derive:

- the `frame` block, whose rules live in `internal/network/codec.go`; those
  values are asserted independently by `verify-architecture.ps1`;
- the C#-facing message names, which come from a hand-maintained table in
  `Tools/protocol/fixture.go` and are cross-asserted against the `MessageId`
  enum by `ProtocolDtoContractTests`.

## Payload shapes

| Shape | Meaning | Messages |
|---|---|---|
| `none` | no payload at all | `1` Ping, `2` Pong, `2003` LeaveQueue, `4011` RokkaActivate |
| `empty` | empty Go struct, serializes to `{}` | `4005`, `4006`, `4008`, `4009` |
| `struct` | fields present | the remaining 31 |

Nullable fields matter. `CardView.Points` and `CardView.RawPoints` are `*int` in
Go: `null` means the server is **hiding** the value. The C# DTOs use `int?` and
tests assert that every field the fixture marks `nullable` maps to a nullable
C# type. Collapsing null into `0` would defeat information hiding.

Protocol negotiation, generated schemas, reconnect/resume semantics, and golden
server-process integration tests are intentionally future work.
````

- [ ] **Step 2: Add the gate row to the verification matrix**

In `docs/verification-matrix.md`, insert this row into the gate table immediately after the `Architecture` row:

```markdown
| Protocol fixture drift | `verify-architecture.ps1` | regenerated `protocol.contract.json` byte-matches the committed file | console |
```

Then append to the end of the "CI boundary" section:

```markdown
The protocol fixture drift gate needs the sibling Go repository. When
`internal/protocol` is absent — as on the hosted CI runner — the gate emits a
warning and is skipped rather than failing, matching the existing CI boundary.
The local aggregate command always runs it.
```

- [ ] **Step 3: Update the migration checklist**

In `docs/migration-checklist.md`, replace the first Phase 3 item:

```markdown
- [ ] Migrate every message as a typed contract before consuming it.
```

with two items:

```markdown
- [x] Every message has a typed contract and a generated, drift-gated fixture.
- [ ] Consume each typed contract from a use case before shipping its feature.
```

- [ ] **Step 4: Run the complete verification**

Run: `.\Tools\ci\verify.ps1`

Expected: the NuGet check passes, architecture verification passes including the new drift gate, EditMode and PlayMode suites pass, `go test ./...` in the Go repository passes, and `Artifacts/verification-summary.md` is written.

Record the actual console output. If any gate fails, fix it before committing — do not commit a red build.

- [ ] **Step 5: Commit**

```bash
git add docs/protocol-contract.md docs/verification-matrix.md docs/migration-checklist.md
git commit -m "Document the generated protocol fixture and its drift gate"
```

---

## Definition of done

- `Tools/protocol` builds, and `go test ./...` passes in that directory.
- `protocol.contract.json` contains 39 messages and 5 nested types, and is byte-identical to a fresh `-out` run.
- 35 payload-carrying messages have registered DTOs; the 4 payload-free messages have none.
- All 5 nested types have registered DTOs matching the fixture.
- Every fixture field carrying a `type_ref` maps to the matching nested DTO, with `IReadOnlyList<T>` unwrapped for repeated fields.
- A real JSON round trip proves `"points": null` stays `null` and stays distinguishable from `"points": 0`.
- `.\Tools\ci\verify.ps1` passes end to end.
- Corrupting any JSON tag name in the fixture makes `verify-architecture.ps1` fail.
