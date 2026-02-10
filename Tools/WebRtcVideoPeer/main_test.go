package main

import (
	"reflect"
	"testing"
)

func TestNormalizeDshowInputArgs_MergesUnquotedVideoDeviceName(t *testing.T) {
	in := []string{
		"-f", "dshow",
		"-i", "video=USB3.0", "Video",
		"-an",
	}

	got := normalizeDshowInputArgs(in)
	want := []string{
		"-f", "dshow",
		"-i", "video=USB3.0 Video",
		"-an",
	}

	if !reflect.DeepEqual(got, want) {
		t.Fatalf("normalizeDshowInputArgs mismatch\n got: %#v\nwant: %#v", got, want)
	}
}

func TestNormalizeDshowInputArgs_QuotesUnquotedSingleTokenWhenNeeded(t *testing.T) {
	in := []string{
		"-f", "dshow",
		"-i", "video=USB3.0 Video",
		"-an",
	}

	got := normalizeDshowInputArgs(in)
	want := []string{
		"-f", "dshow",
		"-i", "video=USB3.0 Video",
		"-an",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("normalizeDshowInputArgs failed to quote dshow device\n got: %#v\nwant: %#v", got, want)
	}
}

func TestNormalizeDshowInputArgs_UnquotesAlreadyQuotedSelector(t *testing.T) {
	in := []string{
		"-f", "dshow",
		"-i", `video="USB3.0 Video"`,
		"-an",
	}

	got := normalizeDshowInputArgs(in)
	want := []string{
		"-f", "dshow",
		"-i", "video=USB3.0 Video",
		"-an",
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("normalizeDshowInputArgs failed to unquote selector\n got: %#v\nwant: %#v", got, want)
	}
}

func TestNormalizeDshowInputArgs_IgnoresNonDshowArgs(t *testing.T) {
	in := []string{
		"-f", "v4l2",
		"-i", "/dev/video0",
	}

	got := normalizeDshowInputArgs(in)
	if !reflect.DeepEqual(got, in) {
		t.Fatalf("normalizeDshowInputArgs changed non-dshow input\n got: %#v\nwant: %#v", got, in)
	}
}
