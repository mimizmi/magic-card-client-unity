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

type PendingAttackView struct {
	AttackerSeat int `json:"attacker_seat"`
}

// NestedShapes exercises pointer/slice combinations and an unexported field.
// The real protocol uses *PendingAttackView today; the other shapes are here so
// the recursion is pinned before something in the server grows one.
type NestedShapes struct {
	PendingAttack  *PendingAttackView `json:"pending_attack,omitempty"`
	PointerToHand  *[]CardView        `json:"pointer_to_hand"`
	SliceOfPointer []*CardView        `json:"slice_of_pointer"`
	unexported     int                `json:"unexported"`
}
