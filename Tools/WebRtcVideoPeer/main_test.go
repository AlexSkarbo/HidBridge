package main

import (
	"reflect"
	"strconv"
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

func TestDefaultVp8EncoderArgs_LowLatencyDiffersFromLow(t *testing.T) {
	low := defaultVp8EncoderArgs("low", 70, 1200, 30)
	lat := defaultVp8EncoderArgs("low-latency", 70, 1200, 30)

	if got := argValue(low, "-cpu-used"); got != "10" {
		t.Fatalf("low cpu-used mismatch, got=%q", got)
	}
	if got := argValue(lat, "-cpu-used"); got != "12" {
		t.Fatalf("low-latency cpu-used mismatch, got=%q", got)
	}
	if got := argValue(low, "-g"); got != "90" {
		t.Fatalf("low gop mismatch, got=%q", got)
	}
	if got := argValue(lat, "-g"); got != "30" {
		t.Fatalf("low-latency gop mismatch, got=%q", got)
	}
}

func TestDefaultH264CpuArgs_LowLatencyDiffersFromLow(t *testing.T) {
	low := defaultH264CpuArgs("low", 70, 1200, 30)
	lat := defaultH264CpuArgs("low-latency", 70, 1200, 30)

	if got := argValue(low, "-preset"); got != "superfast" {
		t.Fatalf("low preset mismatch, got=%q", got)
	}
	if got := argValue(lat, "-preset"); got != "ultrafast" {
		t.Fatalf("low-latency preset mismatch, got=%q", got)
	}
	if got := argValue(low, "-g"); got != "90" {
		t.Fatalf("low gop mismatch, got=%q", got)
	}
	if got := argValue(lat, "-g"); got != "30" {
		t.Fatalf("low-latency gop mismatch, got=%q", got)
	}
}

func TestDefaultVp8EncoderArgs_ImageQualityAddsCrf(t *testing.T) {
	lowQ := defaultVp8EncoderArgs("balanced", 20, 1200, 30)
	highQ := defaultVp8EncoderArgs("balanced", 90, 1200, 30)
	lowCrf := argValue(lowQ, "-crf")
	highCrf := argValue(highQ, "-crf")
	if lowCrf == "" || highCrf == "" {
		t.Fatalf("expected -crf for vp8 args, got low=%q high=%q", lowCrf, highCrf)
	}
	lowN, err1 := strconv.Atoi(lowCrf)
	highN, err2 := strconv.Atoi(highCrf)
	if err1 != nil || err2 != nil {
		t.Fatalf("failed to parse crf values low=%q high=%q", lowCrf, highCrf)
	}
	if !(lowN > highN) {
		t.Fatalf("expected lower CRF for higher image quality, got low=%q high=%q", lowCrf, highCrf)
	}
}

func TestDefaultVp8EncoderArgs_ImageQualityAutoOmitsCrf(t *testing.T) {
	args := defaultVp8EncoderArgs("balanced", 0, 1200, 30)
	if got := argValue(args, "-crf"); got != "" {
		t.Fatalf("expected no -crf for auto image quality, got=%q", got)
	}
}

func TestDefaultH264EncoderArgs_LowLatencyRateControl(t *testing.T) {
	args := defaultH264EncoderArgs("h264_nvenc", "yuv420p", "low-latency", 1200, 30)

	if got := argValue(args, "-g"); got != "30" {
		t.Fatalf("low-latency gop mismatch, got=%q", got)
	}
	if got := argValue(args, "-maxrate"); got != "1260k" {
		t.Fatalf("low-latency maxrate mismatch, got=%q", got)
	}
	if got := argValue(args, "-bufsize"); got != "1200k" {
		t.Fatalf("low-latency bufsize mismatch, got=%q", got)
	}
}

func TestQualityRateControl_LowLatencyScalesWithFps(t *testing.T) {
	gop30, _, _ := qualityRateControl("low-latency", 1200, 30)
	gop60, _, _ := qualityRateControl("low-latency", 1200, 60)
	if gop30 != 30 {
		t.Fatalf("expected gop=30 for 30fps, got=%d", gop30)
	}
	if gop60 != 60 {
		t.Fatalf("expected gop=60 for 60fps, got=%d", gop60)
	}
}

func TestDefaultVp8EncoderArgs_LowLatencyAddsRealtimeFlags(t *testing.T) {
	args := defaultVp8EncoderArgs("low-latency", 70, 1200, 30)
	if got := argValue(args, "-lag-in-frames"); got != "0" {
		t.Fatalf("expected -lag-in-frames 0, got=%q", got)
	}
	if got := argValue(args, "-error-resilient"); got != "1" {
		t.Fatalf("expected -error-resilient 1, got=%q", got)
	}
	if got := argValue(args, "-auto-alt-ref"); got != "0" {
		t.Fatalf("expected -auto-alt-ref 0, got=%q", got)
	}
}

func TestCaptureRtbufsizeForPreset(t *testing.T) {
	if got := captureRtbufsizeForPreset("low-latency"); got != "32M" {
		t.Fatalf("low-latency rtbufsize mismatch, got=%q", got)
	}
	if got := captureRtbufsizeForPreset("low"); got != "64M" {
		t.Fatalf("low rtbufsize mismatch, got=%q", got)
	}
	if got := captureRtbufsizeForPreset("balanced"); got != "128M" {
		t.Fatalf("balanced rtbufsize mismatch, got=%q", got)
	}
	if got := captureRtbufsizeForPreset("high"); got != "128M" {
		t.Fatalf("high rtbufsize mismatch, got=%q", got)
	}
	if got := captureRtbufsizeForPreset("optimal"); got != "256M" {
		t.Fatalf("optimal rtbufsize mismatch, got=%q", got)
	}
}

func argValue(args []string, key string) string {
	for i := 0; i+1 < len(args); i++ {
		if args[i] == key {
			return args[i+1]
		}
	}
	return ""
}
