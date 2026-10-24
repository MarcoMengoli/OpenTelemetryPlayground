"""Baseline guard for application-owned dependencies, source and infrastructure."""
import argparse
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

FORBIDDEN_PACKAGE = re.compile(r"^(?:OpenTelemetry|Aspire)(?:\.|$)", re.I)
INSTRUMENTATION = re.compile(
    r"\b(?:ActivitySource|Meter|MeterProvider|TracerProvider)\b|"
    r"\b(?:AddOpenTelemetry|AddOpenTelemetryTracing|AddOpenTelemetryMetrics|"
    r"UseOpenTelemetry|SetBaggage|InjectTraceContext|ExtractTraceContext)\s*\("
)
DIAGNOSTIC_FIELD = re.compile(r"\b(?:TraceId|SpanId|TraceParent|TraceState|Baggage)\b", re.I)
FORBIDDEN_IMAGE = re.compile(r"image:\s*[^\n]*(?:otel/opentelemetry-collector|grafana/grafana|aspire-dashboard)", re.I)


def check(root: Path, require_restored: bool = False) -> list[str]:
    findings = []

    def report(path, message):
        findings.append(f"{path.relative_to(root)}: {message}")

    projects = sorted(p for p in root.rglob('*.csproj') if not {'bin', 'obj', '.git', '.local'} & set(p.relative_to(root).parts))
    if not projects:
        findings.append('Base US02 assente: nessun progetto .NET; verifica non eseguibile.')
    manifests = projects + [root / name for name in ('Directory.Packages.props', 'Directory.Build.props', 'Directory.Build.targets') if (root / name).exists()]
    for path in manifests:
        try:
            for element in ET.parse(path).iter():
                if element.tag.split('}')[-1] in ('PackageReference', 'PackageVersion', 'Sdk'):
                    package = element.get('Include') or element.get('Update') or element.get('Name') or ''
                    if FORBIDDEN_PACKAGE.match(package):
                        report(path, f'dipendenza vietata: {package}')
                sdk = element.get('Sdk', '')
                if FORBIDDEN_PACKAGE.match(sdk):
                    report(path, f'SDK vietato: {sdk}')
        except ET.ParseError as error:
            report(path, f'XML non valido: {error}')
    for project in projects:
        assets = project.parent / 'obj' / 'project.assets.json'
        if not assets.exists():
            if require_restored:
                report(project, 'assets assenti: eseguire restore prima del controllo transitivo')
            continue
        try:
            for name, library in json.loads(assets.read_text(encoding='utf-8-sig'))['libraries'].items():
                if library.get('type') == 'package' and FORBIDDEN_PACKAGE.match(name.split('/')[0]):
                    report(assets, f'dipendenza risolta vietata: {name}')
        except (ValueError, KeyError, TypeError) as error:
            report(assets, f'assets non validi: {error}')
    for path in (root / 'src').rglob('*.cs'):
        if {'bin', 'obj'} & set(path.relative_to(root).parts):
            continue
        source = path.read_text(encoding='utf-8-sig')
        for line_no, line in enumerate(source.splitlines(), 1):
            if INSTRUMENTATION.search(line):
                report(path, f'{line_no}: possibile instrumentazione personalizzata: {line.strip()}')
            if 'Playground.Contracts' in path.parts and DIAGNOSTIC_FIELD.search(line):
                report(path, f'{line_no}: metadato diagnostico nel contratto: {line.strip()}')
    for path in (root / 'infra').rglob('*'):
        if path.is_file() and path.suffix in ('.yml', '.yaml'):
            if FORBIDDEN_IMAGE.search(path.read_text(encoding='utf-8-sig')):
                report(path, 'infrastruttura OTel/Aspire/dashboard esclusa dalla baseline')
    return findings


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path.cwd())
    parser.add_argument('--require-restored', action='store_true')
    args = parser.parse_args()
    failures = check(args.root.resolve(), args.require_restored)
    for failure in failures:
        print(failure)
    if failures:
        sys.exit(1)
    print('Baseline architetturale: nessuna violazione rilevata (non sostituisce la review).')
