/**
 * Core type definitions for EvalGen
 * Automated evaluation set generator for Copilot connector testing
 */

/** Supported input file formats for evaluation datasets */
export type InputFormat = 'csv' | 'json' | 'jsonl' | 'xlsx' | 'docx' | 'pdf' | 'pptx' | 'txt';

/** A single atomic fact extracted from source data */
export interface Fact {
  id: string;
  field: string;
  value: unknown;
  rowReference: string;
  record: Record<string, unknown>;
}

/** Column profile from dataset analysis */
export interface ColumnProfile {
  name: string;
  dataType: 'string' | 'number' | 'boolean' | 'date' | 'null' | 'mixed';
  nullCount: number;
  uniqueCount: number;
  totalCount: number;
  sampleValues: unknown[];
  /** For categorical columns with low cardinality */
  valueCounts?: Record<string, number>;
  /** Min/max for numeric/date columns */
  min?: unknown;
  max?: unknown;
}

/** Result of profiling a dataset */
export interface DatasetProfile {
  fileName: string;
  format: InputFormat;
  rowCount: number;
  columns: ColumnProfile[];
  /** ~20 representative sample records */
  sampleRecords: Record<string, unknown>[];
  /** Candidate key/identifier columns */
  candidateKeyColumns: string[];
  /** Columns that look like names or titles */
  candidateTitleColumns: string[];
}

/** Question categories appropriate for Copilot connector evals */
export type QuestionCategory =
  | 'single_record_lookup'
  | 'attribute_retrieval'
  | 'filtered_find'
  | 'temporal'
  | 'comparison'
  | 'edge_case';

/** Default category distribution targets */
export const DEFAULT_CATEGORY_WEIGHTS: Record<QuestionCategory, number> = {
  single_record_lookup: 0.30,
  attribute_retrieval: 0.20,
  filtered_find: 0.20,
  temporal: 0.10,
  comparison: 0.10,
  edge_case: 0.10,
};

/** Assertion that validates a Copilot response */
export type Assertion =
  | { type: 'must_contain'; value: string; wholeWord?: boolean }
  | { type: 'must_contain_any'; values: string[] }
  | { type: 'must_not_contain'; value: string };

/** A generated evaluation item with full metadata */
export interface GeneratedEvalItem {
  /** Stable identifier for matching across tools */
  id: string;
  prompt: string;
  expected_answer: string;
  source_location: string;
  assertions: Assertion[];
  category: QuestionCategory;
  difficulty: 'easy' | 'medium' | 'hard';
  supporting_facts: string[];
  grounding_confidence: 'high' | 'medium' | 'low';
  /** All distinct row references the question grounds in (primary + supporting). Used for coverage. */
  referenced_rows?: string[];
}

/** Complete generated evaluation set */
export interface EvalSet {
  version: string;
  generated_at: string;
  description: string;
  source_file: string;
  item_count: number;
  items: GeneratedEvalItem[];
  /** Connector diagnostics warnings (if schema was provided) */
  warnings?: string[];
  /** Generation metadata for reproducibility */
  metadata?: {
    model: string;
    source_hash?: string;
    evalgen_version: string;
    avoidance_evalsets?: string[];
    avoidance_items_compared?: number;
    cross_run_duplicates_removed?: number;
    cross_run_assertion_overlaps?: number;
  };
}

/** LLM-generated question intent (intermediate step) */
export interface QuestionIntent {
  intent: string;
  category: QuestionCategory;
  difficulty: 'easy' | 'medium' | 'hard';
  target_fields: string[];
  target_row_references: string[];
  /** Pre-assigned primary row reference (set by orchestrator before LLM draft) */
  assigned_primary_row?: string;
}

/** LLM-generated question with grounding info */
export interface DraftedQuestion {
  prompt: string;
  category: QuestionCategory;
  difficulty: 'easy' | 'medium' | 'hard';
  referenced_facts: Fact[];
  expected_answer: string;
  supporting_facts: string[];
  source_location: string;
  /** Fact IDs the LLM cited as evidence (preferred; falsy if model didn't return them) */
  supporting_fact_ids?: string[];
  /** Distinct row references actually used as evidence (resolved from supporting_fact_ids or back-matched) */
  referenced_rows?: string[];
  /** Pre-assigned primary row reference (carried from intent) */
  assigned_primary_row?: string;
}

/** Validation result for a generated eval set */
export interface ValidationResult {
  passed: boolean;
  totalItems: number;
  duplicatesRemoved: number;
  categoryBalance: Record<QuestionCategory, number>;
  coverageScore: number;
  issues: string[];
  /** Distinct source rows referenced across the eval set */
  uniqueRowsReferenced?: number;
  /** Total source rows in the dataset */
  totalRows?: number;
  /** Max coverage realistically achievable with this item count (~3 rows touched per question) */
  realisticMaxCoverage?: number;
  /** Question count that would be needed to hit the coverage target on this dataset */
  recommendedCountForTarget?: number;
  /** Whether the dataset is large enough that exhaustive coverage isn't practical */
  datasetSampledNotExhaustive?: boolean;
}

/** CLI options */
export type LLMProvider = 'm365-copilot' | 'm365-copilot-api' | 'workiq-a2a' | 'azure-openai' | 'github-copilot' | 'command';

export interface CliOptions {
  file: string;
  description: string;
  count: number;
  output: string;
  connectorSchema?: string;
  noReview: boolean;
  model: string;
  dryRun?: boolean;
  provider?: LLMProvider;
  llmCommand?: string;
  m365TimeZone?: string;
  m365TenantId?: string;
  extensions?: string[];
  avoidEvalsets?: string[];
}

/** Interface for LLM client */
export interface LLMClient {
  authenticate?(): Promise<void>;
  generateStructured<T>(prompt: string, schema: string): Promise<T>;
  close?(): void;
}
