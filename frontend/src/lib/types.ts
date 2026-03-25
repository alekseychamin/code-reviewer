export type ReviewRunStatus = 'Pending' | 'Running' | 'Completed' | 'Failed';
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
  category: string;
  severity: string;
  title: string;
  description: string;
  existingCode: string;
  suggestion: string;
}

export interface InlineComment {
  id: string;
  filePath: string;
  lineNumber: number;
  title: string;
  severity: string;
  content: string;
  existingCode: string;
  suggestion: string;
  contextBlock: string;
  contextStartLine: number;
  contextEndLine: number;
  relevantDiffHunk: string;
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

export interface ReviewRun {
  id: string;
  status: ReviewRunStatus;
  targetKind: ReviewTargetKind;
  title: string;
  providerProfileId?: string;
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
  inlineComments: InlineComment[];
  reviewedFiles: ReviewedFile[];
}

export interface ChangeDescriptionStructuredContent {
  category: string;
  summary: string;
  impactedModules: string[];
  risks: string[];
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
  providerProfileId?: string;
  publishMode: PublishMode;
  stageOverrides: Array<{ stage: string; profileId: string; model?: string; temperature?: number }>;
}

export interface BranchReviewPayload {
  repositoryPath: string;
  targetBranch: string;
  sourceBranch: string;
  repositoryName?: string;
  providerProfileId?: string;
  publishMode: PublishMode;
  stageOverrides: Array<{ stage: string; profileId: string; model?: string; temperature?: number }>;
}
