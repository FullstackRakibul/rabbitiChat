-- Eval fixture (docs/HR_MODULE_PROMPT.md §8): lines 621-760 of docs/hr/procedures/ProcessFinalSettelment.sql,
-- the Service Length, Gratuity and Service Benefit sections, with the commented-out Ager Formula kept.
	--//Service Length Calculation Star
	SET @LengthYear=(SELECT [dbo].[GetDateDurationYear] (@JoiningDate,@SeparationEffectDate))
	SET @LengthMonth=(SELECT [dbo].[GetDateDurationMonth] (@JoiningDate,@SeparationEffectDate))
	SET @LengthDays=(SELECT [dbo].[GetDateDurationDay] (@JoiningDate,@SeparationEffectDate))
	--//Service Length Calculation ENd


	--//Gratutity Calculation Start
	IF @EmpTypeID=1 AND @EmpCategoryID=1 AND @EmpStatusID <> 2 AND @LengthYear>=5 AND @SeparationEffectDate<'2026-06-28' ---//Updated for Corpore policy provide by taifur vai 
	BEGIN
		IF @LengthYear>=5 AND @LengthMonth>=6 AND @LengthDays>=1
		BEGIN
			SET @LengthYear = @LengthYear+1
		END

		IF @LengthYear BETWEEN 5 AND 9
		BEGIN
			SET @GratuityAmount=(@BasicSalary * (@LengthYear/10.00))*@LengthYear
		END
		IF @LengthYear>9
		BEGIN
			SET @GratuityAmount=@BasicSalary * @LengthYear
		END
	END
	ELSE
	BEGIN
		SET @GratuityAmount=0
	END
	--//Gratutity Calculation End
	--select * from Common_EmploymentStatus where EmpStatusID IN (2,4,13)


	--//Service Benefit Calculation Start

	
	DECLARE @FractionWorkingDay int=(SELECT COUNT (*) FROM Attendance_DailyAttendance WHERE WorkDay BETWEEN DATEADD(YEAR,@LengthYear,@JoiningDate) AND DATEADD(DAY,-1,@SeparationEffectDate) AND EmpID=@EmpId AND AttStatus IN ('P','LT','LTP','SD','TL','EE','NOP','EXL','SL'))

	--IF @FractionWorkingDay>=240 AND @LengthYear >5
	--	BEGIN
	--		SET @LengthYear=@LengthYear+1
	--		SET @LengthMonth=0
	--		SET @LengthDays=0
	--	END

	IF @EmpTypeID IN (2,3,4) AND  @EmpCategoryID=1 AND @EmpStatusID NOT IN (2,4,11,13) AND @LengthYear>=3 AND @SeparationEffectDate<'2026-03-01'
	BEGIN
		
		--Ager Formula
		--IF @LengthYear BETWEEN 5 AND 9
		--BEGIN
		--	IF @LengthMonth>=8 
		--		SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 14) * (@LengthYear+1)
		--	ELSE
		--		SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 14) * @LengthYear
		--END
		--IF @LengthYear>9
		--BEGIN
		--	SET @ServiceBenefitAmount=ROUND(@BasicSalary * @LengthYear,0)
		--END

		---Added by Rakibul Alam for calculating fraction day above or equal 240 ---




		IF @LengthYear BETWEEN 3 AND 9 AND @EmpStatusID  NOT IN (8) --NO Service Benefit for DEATH
		BEGIN

			IF @LengthMonth>=8 
			BEGIN
				IF(@FractionWorkingDay>=240)
				BEGIN

					SET @ServiceBenefitAmount=(((@BasicSalary/30.00) * 15) * (@LengthYear+1))

					
				END
				--ELSE IF @LengthYear=3 AND @FractionWorkingDay<=239
				--BEGIN
				--	SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 7) * @LengthYear
				--END
				BEGIN 
					SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 15) * @LengthYear

				END
			END
			ELSE
			BEGIN
				SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 15) * @LengthYear
			END
		END
		ELSE IF (@LengthYear>=10) -- REF CCL
		BEGIN 
			SET @ServiceBenefitAmount=ROUND((@BasicSalary * @LengthYear),0)
		END

		--IF @EmpStatusID NOT IN (8)
		--BEGIN

		--	IF(@FractionWorkingDay>=240) AND @EmpTypeID IN  (2,3,4)
		--	BEGIN
		--		SET @ServiceBenefitAmount=(((@BasicSalary/30.00) * 14) * (@LengthYear+1))
		--	END
		--	ELSE IF (@FractionWorkingDay>=181) AND @EmpTypeID=1
		--	BEGIN
		--		PRINT 'T'
		--	END
		--END

	END	 
		ELSE IF (@EmpTypeID IN (2,3,4) OR (@EmpTypeID IN (1) AND @SeparationEffectDate>='2026-06-28')) AND  @EmpCategoryID=1 AND @EmpStatusID NOT IN (2,4,11,13) AND @LengthYear>=1 AND @SeparationEffectDate>='2026-03-01'
	BEGIN

		IF @LengthYear BETWEEN 3 AND 9 AND @EmpStatusID  NOT IN (8) --NO Service Benefit for DEATH
		BEGIN

			IF @LengthYear=3 AND (@LengthMonth<1 AND @LengthDays<1)
			BEGIN
					SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 7) * @LengthYear
			END
			ELSE 
			BEGIN
			 	IF @LengthMonth>=6
				BEGIN
					IF(@FractionWorkingDay>=240)
					BEGIN

						SET @ServiceBenefitAmount=(((@BasicSalary/30.00) * 15) * (@LengthYear+1))
					
					END
					ELSE
					BEGIN 
						SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 15) * @LengthYear

					END
				END
				ELSE
				BEGIN
					SET @ServiceBenefitAmount=((@BasicSalary/30.00) * 15) * @LengthYear
				END
