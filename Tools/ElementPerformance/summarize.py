"""离线汇总已停止的 Player 实验；原始 CSV 是证据，不以平均 FPS 隐藏尾部卡顿。"""
import argparse
import csv
import json
from pathlib import Path


def summarize(directory):
    manifest = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))
    summary = json.loads((directory / "summary.json").read_text(encoding="utf-8"))
    final = None
    wet_max = 0.0
    saturated = False
    with (directory / "frames.csv").open(encoding="utf-8", newline="") as source:
        for row in csv.DictReader(source):
            final = row
            if row["phase"] == "4":
                wet_max = max(wet_max, float(row["wet"]))
                saturated |= row["exposureSaturated"] == "True"
    result = {"run": directory.name, "scenario": manifest["scenario"],
              "incomplete": manifest["incomplete"], "hardware": manifest["hardware"],
              "metrics": summary, "wetMaxInMeasure": wet_max,
              "exposureSaturatedInMeasure": saturated, "final": final}
    with (directory / "writes.csv").open(encoding="utf-8", newline="") as source:
        writes = list(csv.DictReader(source))
    result["writeCount"] = len(writes)
    result["rejectedEnqueueCount"] = sum(w["accepted"] != "True" for w in writes)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--after", required=True, help="只收录此 UTC 后完成的目录，例如 20260905-174000")
    args = parser.parse_args()
    results = []
    for directory in sorted(args.root.iterdir()):
        if not directory.is_dir() or not (directory / "summary.json").is_file():
            continue
        # 目录末尾固定为 yyyyMMdd-HHmmss-fff，不依赖含连字符的场景名长度。
        if directory.name[-19:] < args.after:
            continue
        results.append(summarize(directory))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    print("run,p50_ms,p99_ms,alive,dropped,wet_max,gc_max,incomplete")
    for result in results:
        interval = result["metrics"]["intervalMs"] or {}
        gc = result["metrics"]["gcBytes"] or {}
        final = result["final"] or {}
        print(",".join(str(v) for v in (result["run"], interval.get("p50"), interval.get("p99"),
              final.get("alive"), final.get("dropped"), result["wetMaxInMeasure"],
              gc.get("max"), result["incomplete"])))


if __name__ == "__main__":
    main()
