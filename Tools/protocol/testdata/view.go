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
