package protocol

// BadField carries a Go type that cannot appear in a JSON wire contract.
// ParseStructs must reject it by name rather than describing it as
// "unsupported" and letting a wrong value reach the fixture.
type BadField struct {
	Events chan int `json:"events"`
}
