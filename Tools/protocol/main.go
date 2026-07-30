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
