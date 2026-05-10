-- Run manually if the database stored ReviewFindingSource as text and contained DiffRiskHint
-- before enum value was removed (adjust table/column names to match your schema).

-- UPDATE review_findings SET source = 'InitialReview' WHERE source::text = 'DiffRiskHint';
