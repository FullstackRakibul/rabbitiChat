ALTER FUNCTION [dbo].[GetDateDurationYear] 
(	
	@fromDate date,@toDate date
)
RETURNS int
AS
BEGIN
	DECLARE @date datetime, @tmpdate datetime, @years int
	SELECT @date = @fromDate
	SELECT @tmpdate = @date
	SELECT @years = DATEDIFF(yy, @tmpdate, @toDate) - CASE WHEN (MONTH(@date) > MONTH(@toDate)) OR (MONTH(@date) = MONTH(@toDate) AND DAY(@date) > DAY(@toDate)) THEN 1 ELSE 0 END	
	RETURN @years
END
