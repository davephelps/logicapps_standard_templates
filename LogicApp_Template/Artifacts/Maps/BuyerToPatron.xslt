<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="3.0" 
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:src="http://EDITest.SourceBuyer"
    xmlns:tgt="http://EDITest.TargetPatron"
    xmlns:xs="http://www.w3.org/2001/XMLSchema"
    exclude-result-prefixes="src xs">
    
    <xsl:output method="xml" indent="yes" encoding="UTF-8"/>
    
    <xsl:template match="/">
        <tgt:PatronRegistry>
            <xsl:apply-templates select="src:Buyer/src:BuyerProfile"/>
        </tgt:PatronRegistry>
    </xsl:template>
    
    <xsl:template match="src:BuyerProfile">
        <tgt:Patron>
            <tgt:CompleteName>
                <xsl:value-of select="concat(src:FirstName, ' ', src:LastName)"/>
            </tgt:CompleteName>
            <tgt:DateOfBirth>
                <xsl:value-of select="src:BirthDate"/>
            </tgt:DateOfBirth>
            <tgt:GivenName>
                <xsl:value-of select="src:FirstName"/>
            </tgt:GivenName>
            <tgt:FamilyName>
                <xsl:value-of select="src:LastName"/>
            </tgt:FamilyName>
            <tgt:MembershipType>Standard</tgt:MembershipType>
            <tgt:Honorific>
                <xsl:value-of select="src:Salutation"/>
            </tgt:Honorific>
            <tgt:LocationInfo>
                <tgt:roadName>
                    <xsl:value-of select="src:Location/src:streetAddress"/>
                </tgt:roadName>
            </tgt:LocationInfo>
        </tgt:Patron>
    </xsl:template>
    
</xsl:stylesheet>
