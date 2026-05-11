export type ReviewRunStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';
export type ReviewTargetKind = 'PullRequest' | 'BranchComparison';
export type PublishMode = 'None' | 'SummaryOnly' | 'SummaryAndInline';

export interface ProviderProfile {
  id: string;
  name: string;
  kind: 'OpenAiCompatible' | 'Ollama';
  baseUrl: string;
  defaultModel: string;
  supportsStructuredOutput: boolean;
  localOnly: boolean;
}

export interface ReviewFinding {
  file: string;
  lineHint: string;
  startLine: number;
  endLine: number;
  category: string;
  severity: string;
  source: string;
  title: string;
  description: string;
  existingCode: string;
  suggestion: string;
}

export interface InlineComment {
  id: string;
  findingId: string;
  filePath: string;
  lineNumber: number;
  title: string;
  severity: string;
  category: string;
  source: string;
  content: string;
  existingCode: string;
  suggestion: string;
  startLine: number;
  endLine: number;
  contextBlock: string;
  contextStartLine: number;
  contextEndLine: number;
  relevantDiffHunk: string;
  isRelevant: boolean;
  publishedToTfs: boolean;
  publishedAt?: string;
  messages: ReviewCommentMessage[];
}

export interface ReviewCommentMessage {
  role: string;
  content: string;
  createdAt: string;
  structuredContent?: InlineDiscussionStructuredContent;
}

export interface InlineDiscussionStructuredContent {
  summary: string;
  problems: string[];
  risk: string;
  recommendations: string[];
  shouldPublishToTfs?: boolean;
  publishToTfsReason: string;
  exampleCodeLanguage: string;
  exampleCode: string;
  addedFindingsCount: number;
  addedFindings: string[];
  addedOpportunitiesCount: number;
  addedOpportunities: string[];
  addedOpportunityItems: InlineDiscussionOpportunityItem[];
}

export interface InlineDiscussionOpportunityItem {
  file: string;
  lineHint: string;
  startLine: number;
  title: string;
  description: string;
}

export interface ReviewOpportunityItem {
  file: string;
  lineHint: string;
  startLine: number;
  title: string;
  description: string;
  suggestion: string;
}

export interface ReviewedFile {
  filePath: string;
  displayName: string;
  changeType: string;
  addedLines: number;
  deletedLines: number;
  diffPatch: string;
  fullContent: string;
  changedLineNumbers: number[];
  inlineThreads: InlineComment[];
}

export interface SemanticCodeContext {
  enabled: boolean;
  attempted: boolean;
  succeeded: boolean;
  timedOut: boolean;
  cacheReuseEnabled: boolean;
  sourceCacheHit: boolean;
  targetCacheHit: boolean;
  sourceFilesSelected: number;
  targetFilesSelected: number;
  sourceFilesIndexed: number;
  targetFilesIndexed: number;
  sourceChunksIndexed: number;
  targetChunksIndexed: number;
  sourceFilesMissing: number;
  targetFilesMissing: number;
  sourceFilesTooLarge: number;
  targetFilesTooLarge: number;
  sourceFilesEmpty: number;
  targetFilesEmpty: number;
  sourceFilesWithoutChunks: number;
  targetFilesWithoutChunks: number;
  sourceFilesReadFailed: number;
  targetFilesReadFailed: number;
  sourceFilesSkippedDeleted: number;
  targetFilesSkippedAdded: number;
  targetBaselineFilesSelected: number;
  queryCount: number;
  candidateCount: number;
  snippetCount: number;
  elapsedMilliseconds: number;
  status: string;
  message: string;
  sourceCommitSha?: string;
  targetCommitSha?: string;
  snippets: SemanticCodeContextSnippet[];
}

export interface SemanticCodeContextSnippet {
  revisionKind: string;
  commitSha: string;
  filePath: string;
  startLine: number;
  endLine: number;
  score: number;
  query: string;
}

export interface ReviewRun {
  id: string;
  status: ReviewRunStatus;
  targetKind: ReviewTargetKind;
  title: string;
  pullRequestUrl?: string;
  repositoryName?: string;
  sourceBranch?: string;
  targetBranch?: string;
  providerProfileId?: string;
  serviceName: string;
  authorName?: string;
  currentStage?: string;
  progressPercent: number;
  currentMessage: string;
  errorMessage?: string;
  createdAt: string;
  updatedAt: string;
  changeDescription: string;
  changeDescriptionStructured?: ChangeDescriptionStructuredContent;
  changeDiagramMermaid?: string;
  hasDiffArtifact: boolean;
  markdownReport: string;
  hasMarkdownReportArtifact: boolean;
  summaryComment: string;
  publishSucceeded: boolean;
  changedFiles: string[];
  findings: ReviewFinding[];
  primaryOpportunities: ReviewOpportunityItem[];
  externalReview: ExternalReview;
  semanticCodeContext: SemanticCodeContext;
  reviewDiscussionMessages: ReviewCommentMessage[];
  inlineComments: InlineComment[];
  reviewedFiles: ReviewedFile[];
  progressUpdates: ReviewProgressEvent[];
}

export interface ExternalReview {
  enabled: boolean;
  attempted: boolean;
  succeeded: boolean;
  timedOut: boolean;
  engineName: string;
  status: string;
  message: string;
  elapsedMilliseconds: number;
  startedAt?: string;
  completedAt?: string;
  commands: ExternalReviewCommand[];
}

export interface ExternalReviewCommand {
  command: string;
  succeeded: boolean;
  elapsedMilliseconds: number;
  artifact: string;
  errorMessage: string;
}

export interface ReviewHistory {
  baselineRunId?: string;
  items: ReviewHistoryItem[];
}

export interface ReviewHistoryItem {
  id: string;
  status: ReviewRunStatus;
  targetKind: ReviewTargetKind;
  title: string;
  pullRequestUrl?: string;
  repositoryName?: string;
  sourceBranch?: string;
  targetBranch?: string;
  serviceName: string;
  authorName?: string;
  providerProfileId?: string;
  createdAt: string;
  updatedAt: string;
  findingsCount: number;
  criticalCount: number;
  highCount: number;
  publishSucceeded: boolean;
  hasMarkdownReportArtifact: boolean;
}

export interface ChangeDescriptionStructuredContent {
  category: string;
  summary: string;
  impactedModules: string[];
  risks: string[];
  estimatedReviewEffort?: number;
  qualityScore?: number;
}

export interface ReviewProgressEvent {
  runId: string;
  status: ReviewRunStatus;
  stage?: string;
  progressPercent: number;
  message: string;
  timestamp: string;
  isTerminal: boolean;
}

export interface PullRequestReviewPayload {
  pullRequestUrl: string;
  accessToken?: string;
  providerProfileId?: string;
  publishMode: PublishMode;
  forceRerun?: boolean;
  baselineRunId?: string;
  stageOverrides: Array<{ stage: string; profileId: string; model?: string; temperature?: number }>;
}

export interface BranchReviewPayload {
  repositoryName: string;
  targetBranch: string;
  sourceBranch: string;
  providerProfileId?: string;
  publishMode: PublishMode;
  forceRerun?: boolean;
  baselineRunId?: string;
  stageOverrides: Array<{ stage: string; profileId: string; model?: string; temperature?: number }>;
}
