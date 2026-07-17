/**
 * Extracts the description from a SKILL.md file.
 *
 * Priority:
 *  1. YAML frontmatter `description:` field
 *  2. First non-empty prose line (skips headings, fences, directives)
 */
export function parseSkillDescription(content: string): string {
  // 1. YAML frontmatter block
  const fmMatch = content.match(/^---\r?\n([\s\S]*?)\r?\n---/)
  if (fmMatch) {
    const description = parseFrontmatterDescription(fmMatch[1])
    if (description) return description
  }

  // 2. First meaningful prose line
  for (const raw of content.split(/\r?\n/)) {
    const line = raw.trim()
    if (
      line.length > 0 &&
      !line.startsWith('#') &&
      !line.startsWith('---') &&
      !line.startsWith('```') &&
      !line.startsWith('<!--') &&
      !line.startsWith('>') &&
      !line.startsWith('|')
    ) {
      return line
    }
  }

  return ''
}

function parseFrontmatterDescription(frontmatter: string): string {
  const lines = frontmatter.split(/\r?\n/)

  for (let i = 0; i < lines.length; i++) {
    const match = lines[i].match(/^description:\s*(.*)$/)
    if (!match) continue

    const value = match[1].trim()
    if (isYamlBlockScalar(value)) {
      return normalizeDescription(collectYamlBlock(lines, i + 1))
    }

    return normalizeDescription(stripYamlQuotes(value))
  }

  return ''
}

function isYamlBlockScalar(value: string): boolean {
  return /^[|>][+-]?$/.test(value)
}

function stripYamlQuotes(value: string): string {
  if (value.length >= 2) {
    const first = value[0]
    const last = value[value.length - 1]
    if ((first === '"' && last === '"') || (first === "'" && last === "'")) {
      return value.slice(1, -1)
    }
  }
  return value
}

function collectYamlBlock(lines: string[], startIndex: number): string {
  const blockLines: string[] = []

  for (let i = startIndex; i < lines.length; i++) {
    const line = lines[i]
    if (/^[A-Za-z][A-Za-z0-9-]*\s*:/.test(line)) break
    if (line.trim().length === 0) {
      blockLines.push('')
      continue
    }
    if (!/^[ \t]+/.test(line)) break
    blockLines.push(line)
  }

  const minIndent = blockLines
    .filter((line) => line.trim().length > 0)
    .reduce((min, line) => {
      const indent = line.match(/^[ \t]*/)?.[0].length ?? 0
      return Math.min(min, indent)
    }, Number.POSITIVE_INFINITY)

  if (!Number.isFinite(minIndent)) return ''
  return blockLines.map((line) => line.slice(minIndent)).join('\n')
}

function normalizeDescription(value: string): string {
  return value.replace(/\s+/g, ' ').trim()
}
